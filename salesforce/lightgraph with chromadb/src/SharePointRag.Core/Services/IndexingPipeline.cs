using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Threading.Channels;

namespace SharePointRag.Core.Services;

/// <summary>
/// Batching-aware ingestion pipeline scoped to a single named RAG system.
///
/// ── What changed vs the naive record-at-a-time design ─────────────────────────
///
/// Previous design:
///   for each SourceRecord:
///     text = extract()
///     chunks = chunk(text)
///     embeddings = EmbedBatch(chunks)   ← one API call per record
///     LiteGraph.Node.Create × N         ← N individual SQLite writes per record
///
/// New design — two independent batch windows:
///
///   ┌─ Source channel ──────────────────────────────────────────────────────┐
///   │  Producer: connector streams SourceRecord → bounded channel          │
///   │  Consumers: N workers extract + chunk, producing PendingChunk items  │
///   └────────────────────────────────────────────────────────────────────────┘
///               │  PendingChunk channel (text, no embedding yet)
///               ▼
///   ┌─ Embedding aggregator ─────────────────────────────────────────────────┐
///   │  Accumulates PendingChunks until EmbeddingBatchSize is reached        │
///   │  OR FlushTimeoutMs elapses (handles end-of-source tail)               │
///   │  → single EmbedBatchAsync() call for the whole window                 │
///   └────────────────────────────────────────────────────────────────────────┘
///               │  ReadyChunk channel (text + embedding)
///               ▼
///   ┌─ LiteGraph upsert aggregator ──────────────────────────────────────────┐
///   │  Accumulates ReadyChunks until UpsertBatchSize is reached             │
///   │  → single UpsertAsync() call writing all nodes in the batch           │
///   │  → state store updated per-record after its chunks are written        │
///   └────────────────────────────────────────────────────────────────────────┘
///
/// Memory profile:
///   Peak ≈ EmbeddingBatchSize chunks in the embedding window
///         + UpsertBatchSize chunks in the upsert window
///         + bounded source channel (CrawlParallelism × 4 records)
///   With defaults: ~256 chunks × 512 tokens × ~4 bytes ≈ ~500 KB in flight.
///   The full corpus is never held in memory simultaneously.
/// </summary>
public sealed class IndexingPipeline : IIndexingPipeline
{
    private readonly RagSystemDefinition                 _system;
    private readonly IReadOnlyList<IDataSourceConnector> _connectors;
    private readonly IReadOnlyList<DataSourceDefinition> _sourceDefs;
    private readonly ITextExtractor                      _extractor;
    private readonly ITextChunker                        _chunker;
    private readonly IEmbeddingService                   _embedder;
    private readonly IVectorStore                        _vectorStore;
    private readonly IIndexStateStore                    _stateStore;
    private readonly BatchingOptions                     _batching;
    private readonly ILogger<IndexingPipeline>           _logger;

    public string SystemName => _system.Name;

    public IndexingPipeline(
        RagSystemDefinition system,
        IEnumerable<(DataSourceDefinition Def, IDataSourceConnector Connector)> sources,
        ITextExtractor extractor,
        ITextChunker chunker,
        IEmbeddingService embedder,
        IVectorStore vectorStore,
        IIndexStateStore stateStore,
        BatchingOptions batching,
        ILogger<IndexingPipeline> logger)
    {
        _system      = system;
        _sourceDefs  = sources.Select(s => s.Def).ToList().AsReadOnly();
        _connectors  = sources.Select(s => s.Connector).ToList().AsReadOnly();
        _extractor   = extractor;
        _chunker     = chunker;
        _embedder    = embedder;
        _vectorStore = vectorStore;
        _stateStore  = stateStore;
        _batching    = batching;
        _logger      = logger;
    }

    // ── Public entry points ───────────────────────────────────────────────────

    public async Task RunFullIndexAsync(CancellationToken ct = default)
    {
        await _vectorStore.CreateIndexIfNotExistsAsync(ct);
        _logger.LogInformation("[{Sys}] FULL index — {N} source(s)", _system.Name, _connectors.Count);

        for (int i = 0; i < _connectors.Count; i++)
        {
            var connector = _connectors[i];
            var def       = _sourceDefs[i];
            _logger.LogInformation("[{Sys}] Full-indexing '{Src}' ({Type})",
                _system.Name, connector.DataSourceName, connector.ConnectorType);

            await RunBatchPipelineAsync(connector, def, connector.GetRecordsAsync(ct), ct);
            await _stateStore.SetLastFullIndexTimeAsync(
                connector.DataSourceName, DateTimeOffset.UtcNow, ct);
        }

        _logger.LogInformation("[{Sys}] Full index complete.", _system.Name);
    }

    public async Task RunDeltaIndexAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[{Sys}] DELTA index — {N} source(s)", _system.Name, _connectors.Count);

        for (int i = 0; i < _connectors.Count; i++)
        {
            var connector = _connectors[i];
            var def       = _sourceDefs[i];

            var since = await _stateStore.GetLastFullIndexTimeAsync(connector.DataSourceName, ct)
                        ?? DateTimeOffset.UtcNow.AddDays(-7);

            IAsyncEnumerable<SourceRecord> records = def.DeltaSupported
                ? connector.GetModifiedRecordsAsync(since, ct)
                : connector.GetRecordsAsync(ct);

            _logger.LogInformation(
                "[{Sys}] Delta '{Src}' since {Since} (deltaSupported={D})",
                _system.Name, connector.DataSourceName, since, def.DeltaSupported);

            await RunBatchPipelineAsync(connector, def, records, ct);
        }

        _logger.LogInformation("[{Sys}] Delta index complete.", _system.Name);
    }

    // ── Three-stage batch pipeline ────────────────────────────────────────────

    private async Task RunBatchPipelineAsync(
        IDataSourceConnector         connector,
        DataSourceDefinition         def,
        IAsyncEnumerable<SourceRecord> records,
        CancellationToken            ct)
    {
        // Stage 1 → Stage 2 channel: source records waiting to be chunked
        var sourceChannel = Channel.CreateBounded<SourceRecord>(
            new BoundedChannelOptions(Math.Max(8, def.CrawlParallelism * 4))
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

        // Stage 2 → Stage 3 channel: chunked text waiting for embedding
        // Capacity = 2× the embedding batch size to keep the embedder fed
        var pendingChannel = Channel.CreateBounded<PendingChunk>(
            new BoundedChannelOptions(_batching.EmbeddingBatchSize * 2)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });

        // Stage 3 → sink channel: embedded chunks waiting to be written
        var readyChannel = Channel.CreateBounded<ReadyChunk>(
            new BoundedChannelOptions(_batching.UpsertBatchSize * 2)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true
            });

        // ── Stage 1: Producer — stream source records into the source channel ─
        var producer = Task.Run(async () =>
        {
            await foreach (var r in records.WithCancellation(ct))
                await sourceChannel.Writer.WriteAsync(r, ct);
            sourceChannel.Writer.Complete();
        }, ct);

        // ── Stage 2: Chunkers — extract text, chunk, enqueue PendingChunks ───
        var chunkers = Enumerable
            .Range(0, Math.Max(1, def.CrawlParallelism))
            .Select(_ => Task.Run(
                () => ChunkingWorkerAsync(connector, sourceChannel.Reader, pendingChannel.Writer, ct),
                ct))
            .ToArray();

        // Complete pendingChannel after all chunkers finish
        var chunkingDone = Task.WhenAll(chunkers).ContinueWith(_ =>
            pendingChannel.Writer.TryComplete(), TaskScheduler.Default);

        // ── Stage 3: Embedder — batch PendingChunks → produce ReadyChunks ────
        var embedder = Task.Run(
            () => EmbeddingAggregatorAsync(pendingChannel.Reader, readyChannel.Writer, ct),
            ct);

        // ── Stage 4: Upsert sink — batch ReadyChunks → write to LiteGraph ───
        var upserter = Task.Run(
            () => UpsertAggregatorAsync(readyChannel.Reader, ct),
            ct);

        await producer;
        await chunkingDone;
        await embedder;
        await upserter;
    }

    // ── Stage 2: Chunking workers ─────────────────────────────────────────────

    private async Task ChunkingWorkerAsync(
        IDataSourceConnector        connector,
        ChannelReader<SourceRecord> reader,
        ChannelWriter<PendingChunk> writer,
        CancellationToken           ct)
    {
        await foreach (var record in reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessOneRecordAsync(connector, record, writer, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Sys}/{Src}] Chunking failed for '{Id}'",
                    _system.Name, connector.DataSourceName, record.Id);

                await _stateStore.SaveAsync(new IndexingRecord
                {
                    SourceId       = record.Id,
                    DataSourceName = record.DataSourceName,
                    RagSystemName  = _system.Name,
                    Title          = record.Title,
                    Status         = IndexingStatus.Failed,
                    ErrorMessage   = ex.Message,
                    LastIndexed    = DateTimeOffset.UtcNow
                }, ct);
            }
        }
    }

    private async Task ProcessOneRecordAsync(
        IDataSourceConnector        connector,
        SourceRecord                record,
        ChannelWriter<PendingChunk> writer,
        CancellationToken           ct)
    {
        // Skip unchanged records (delta optimisation — no memory cost)
        var existing = await _stateStore.GetAsync(record.Id, record.DataSourceName, ct);
        if (existing?.Status == IndexingStatus.Indexed
            && existing.LastIndexed >= record.LastModified)
        {
            _logger.LogDebug("[{Sys}/{Src}] Skipping unchanged '{Id}'",
                _system.Name, record.DataSourceName, record.Id);
            return;
        }

        // Extract text from binary content if needed — then immediately release the stream
        var text = record.Content;
        if (string.IsNullOrWhiteSpace(text) && record.RawContent != null)
        {
            text = await _extractor.ExtractAsync(
                record.RawContent, record.MimeType, record.Title, ct);
            // RawContent (binary stream) is no longer referenced after this point
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await _stateStore.SaveAsync(new IndexingRecord
            {
                SourceId       = record.Id,
                DataSourceName = record.DataSourceName,
                RagSystemName  = _system.Name,
                Title          = record.Title,
                Status         = IndexingStatus.Skipped,
                LastIndexed    = DateTimeOffset.UtcNow
            }, ct);
            return;
        }

        // Chunk the text — each chunk is a lightweight string slice
        var textChunks = _chunker.Chunk(text);
        text = null!; // allow GC of the full text before embedding

        var enrichedMeta = new Dictionary<string, string>(record.Metadata)
        {
            ["ConnectorType"] = connector.ConnectorType.ToString()
        };

        // Enqueue one PendingChunk per text chunk.
        // Only the metadata shell and text slice are held — no embeddings yet.
        for (int i = 0; i < textChunks.Count; i++)
        {
            await writer.WriteAsync(new PendingChunk(
                SourceId:       record.Id,
                DataSourceName: record.DataSourceName,
                Title:          record.Title,
                Url:            record.Url,
                Author:         record.Author,
                LastModified:   record.LastModified,
                Text:           textChunks[i],
                ChunkIndex:     i,
                TotalChunks:    textChunks.Count,
                Metadata:       enrichedMeta
            ), ct);
        }

        _logger.LogDebug("[{Sys}/{Src}] Chunked '{Title}' → {N} chunks pending embedding",
            _system.Name, record.DataSourceName, record.Title, textChunks.Count);
    }

    // ── Stage 3: Embedding aggregator ─────────────────────────────────────────

    private async Task EmbeddingAggregatorAsync(
        ChannelReader<PendingChunk> reader,
        ChannelWriter<ReadyChunk>   writer,
        CancellationToken           ct)
    {
        var buffer  = new List<PendingChunk>(_batching.EmbeddingBatchSize);
        var timeout = TimeSpan.FromMilliseconds(_batching.FlushTimeoutMs);

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                // Drain as many as we can up to EmbeddingBatchSize, but don't
                // block longer than FlushTimeoutMs
                var deadline = DateTimeOffset.UtcNow.Add(timeout);

                while (buffer.Count < _batching.EmbeddingBatchSize
                       && DateTimeOffset.UtcNow < deadline)
                {
                    if (!reader.TryRead(out var chunk))
                    {
                        // Nothing available right now — wait briefly then check deadline
                        await Task.Delay(10, ct);
                        break;
                    }
                    buffer.Add(chunk);
                }

                if (buffer.Count == 0) continue;

                await FlushEmbeddingBufferAsync(buffer, writer, ct);
            }

            // Drain anything remaining after the channel is completed
            while (reader.TryRead(out var remaining))
                buffer.Add(remaining);

            if (buffer.Count > 0)
                await FlushEmbeddingBufferAsync(buffer, writer, ct);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task FlushEmbeddingBufferAsync(
        List<PendingChunk>        buffer,
        ChannelWriter<ReadyChunk> writer,
        CancellationToken         ct)
    {
        var texts = buffer.Select(c => c.Text).ToList();

        _logger.LogDebug(
            "[{Sys}] Embedding batch of {N} chunks (from {Sources} source(s))",
            _system.Name, texts.Count,
            buffer.Select(c => c.SourceId).Distinct().Count());

        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await _embedder.EmbedBatchAsync(texts, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{Sys}] Embedding batch failed — marking {N} chunks as failed",
                _system.Name, buffer.Count);

            // Mark every source record that contributed to this batch as failed
            foreach (var srcId in buffer.Select(c => (c.SourceId, c.DataSourceName)).Distinct())
                await _stateStore.SaveAsync(new IndexingRecord
                {
                    SourceId       = srcId.SourceId,
                    DataSourceName = srcId.DataSourceName,
                    RagSystemName  = _system.Name,
                    Status         = IndexingStatus.Failed,
                    ErrorMessage   = ex.Message,
                    LastIndexed    = DateTimeOffset.UtcNow
                }, ct);

            buffer.Clear();
            return;
        }

        // Forward embedded chunks downstream; release text strings promptly
        for (int i = 0; i < buffer.Count; i++)
        {
            var pending = buffer[i];
            await writer.WriteAsync(new ReadyChunk(
                SourceId:       pending.SourceId,
                DataSourceName: pending.DataSourceName,
                Title:          pending.Title,
                Url:            pending.Url,
                Author:         pending.Author,
                LastModified:   pending.LastModified,
                Text:           pending.Text,
                ChunkIndex:     pending.ChunkIndex,
                TotalChunks:    pending.TotalChunks,
                Metadata:       pending.Metadata,
                Embedding:      embeddings[i]
            ), ct);
        }

        buffer.Clear();   // release PendingChunk references → GC can reclaim text strings
    }

    // ── Stage 4: Upsert aggregator ────────────────────────────────────────────

    private async Task UpsertAggregatorAsync(
        ChannelReader<ReadyChunk> reader,
        CancellationToken         ct)
    {
        var buffer = new List<ReadyChunk>(_batching.UpsertBatchSize);

        // Track which source records have had all their chunks buffered
        // so we can update state store only when a full record is written
        var chunksSeen = new Dictionary<string, (int Seen, int Total, ReadyChunk LastChunk)>();

        await foreach (var chunk in reader.ReadAllAsync(ct))
        {
            buffer.Add(chunk);

            // Track chunk progress per source record
            var key = $"{chunk.DataSourceName}::{chunk.SourceId}";
            if (!chunksSeen.TryGetValue(key, out var progress))
                progress = (0, chunk.TotalChunks, chunk);
            chunksSeen[key] = (progress.Seen + 1, chunk.TotalChunks, chunk);

            if (buffer.Count >= _batching.UpsertBatchSize)
                await FlushUpsertBufferAsync(buffer, chunksSeen, ct);
        }

        // Flush tail
        if (buffer.Count > 0)
            await FlushUpsertBufferAsync(buffer, chunksSeen, ct);
    }

    private async Task FlushUpsertBufferAsync(
        List<ReadyChunk>                                                     buffer,
        Dictionary<string, (int Seen, int Total, ReadyChunk LastChunk)>     chunksSeen,
        CancellationToken                                                    ct)
    {
        if (buffer.Count == 0) return;

        var chunks = buffer.Select(rc => new DocumentChunk
        {
            SourceId       = rc.SourceId,
            DataSourceName = rc.DataSourceName,
            Title          = rc.Title,
            Url            = rc.Url,
            Author         = rc.Author,
            LastModified   = rc.LastModified,
            Content        = rc.Text,
            ChunkIndex     = rc.ChunkIndex,
            TotalChunks    = rc.TotalChunks,
            Metadata       = rc.Metadata,
            Embedding      = rc.Embedding
        }).ToList();

        _logger.LogDebug(
            "[{Sys}] Writing batch of {N} chunks to LiteGraph",
            _system.Name, chunks.Count);

        // First delete old versions of any source records in this batch
        var sourceRecordsInBatch = buffer
            .Select(c => (c.SourceId, c.DataSourceName))
            .Distinct()
            .ToList();

        foreach (var (sourceId, dsName) in sourceRecordsInBatch)
            await _vectorStore.DeleteBySourceIdAsync(sourceId, dsName, ct);

        // Write the whole batch in one UpsertAsync call
        await _vectorStore.UpsertAsync(chunks, ct);

        // Update state store for every source record whose last chunk was included
        foreach (var (key, (seen, total, lastChunk)) in chunksSeen)
        {
            if (seen >= total)
            {
                await _stateStore.SaveAsync(new IndexingRecord
                {
                    SourceId       = lastChunk.SourceId,
                    DataSourceName = lastChunk.DataSourceName,
                    RagSystemName  = _system.Name,
                    Title          = lastChunk.Title,
                    Status         = IndexingStatus.Indexed,
                    ChunkCount     = total,
                    LastIndexed    = DateTimeOffset.UtcNow
                }, ct);

                _logger.LogInformation(
                    "[{Sys}/{Src}] Indexed '{Title}' ({N} chunks)",
                    _system.Name, lastChunk.DataSourceName, lastChunk.Title, total);
            }
        }

        // Remove completed records from tracking; keep partial ones for next flush
        var completed = chunksSeen
            .Where(kv => kv.Value.Seen >= kv.Value.Total)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in completed) chunksSeen.Remove(k);

        buffer.Clear();   // release ReadyChunk references; embeddings are now in LiteGraph
    }

    // ── Lightweight intermediate records ──────────────────────────────────────
    // These are structs-by-design: one PendingChunk per text slice (no embedding),
    // one ReadyChunk per embedded slice. Using records keeps them on the heap but
    // allows the GC to collect them as soon as the buffer is cleared.

    private sealed record PendingChunk(
        string                     SourceId,
        string                     DataSourceName,
        string                     Title,
        string                     Url,
        string?                    Author,
        DateTimeOffset             LastModified,
        string                     Text,           // ← the only large field
        int                        ChunkIndex,
        int                        TotalChunks,
        Dictionary<string, string> Metadata
    );

    private sealed record ReadyChunk(
        string                     SourceId,
        string                     DataSourceName,
        string                     Title,
        string                     Url,
        string?                    Author,
        DateTimeOffset             LastModified,
        string                     Text,
        int                        ChunkIndex,
        int                        TotalChunks,
        Dictionary<string, string> Metadata,
        float[]                    Embedding       // ← populated after EmbedBatchAsync
    );
}
