using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Ingestion pipeline scoped to a single named RAG system.
///
/// For each assigned data source connector:
///   Full  → GetRecordsAsync     → extract → chunk → embed → upsert, one record at a time
///   Delta → GetModifiedRecordsAsync (falls back to full if DeltaSupported=false)
///
/// Parallel crawling: N workers (= DataSourceDefinition.CrawlParallelism) process
/// source records concurrently. Each worker runs the full extract→chunk→embed→upsert
/// cycle independently, so there is no shared mutable state between workers.
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

            await ProcessSourceAsync(connector, def, connector.GetRecordsAsync(ct), ct);
            await _stateStore.SetLastFullIndexTimeAsync(connector.DataSourceName, DateTimeOffset.UtcNow, ct);
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

            _logger.LogInformation("[{Sys}] Delta '{Src}' since {Since} (deltaSupported={D})",
                _system.Name, connector.DataSourceName, since, def.DeltaSupported);

            await ProcessSourceAsync(connector, def, records, ct);
        }

        _logger.LogInformation("[{Sys}] Delta index complete.", _system.Name);
    }

    // ── Per-source processing ─────────────────────────────────────────────────

    private async Task ProcessSourceAsync(
        IDataSourceConnector           connector,
        DataSourceDefinition           def,
        IAsyncEnumerable<SourceRecord> records,
        CancellationToken              ct)
    {
        var parallelism = Math.Max(1, def.CrawlParallelism);
        var semaphore   = new SemaphoreSlim(parallelism, parallelism);
        var tasks       = new List<Task>();

        await foreach (var record in records.WithCancellation(ct))
        {
            await semaphore.WaitAsync(ct);
            var captured = record;
            tasks.Add(Task.Run(async () =>
            {
                try   { await IndexRecordAsync(connector, captured, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Sys}/{Src}] Failed to index '{Id}'",
                        _system.Name, connector.DataSourceName, captured.Id);

                    await _stateStore.SaveAsync(new IndexingRecord
                    {
                        SourceId       = captured.Id,
                        DataSourceName = captured.DataSourceName,
                        RagSystemName  = _system.Name,
                        Title          = captured.Title,
                        Status         = IndexingStatus.Failed,
                        ErrorMessage   = ex.Message,
                        LastIndexed    = DateTimeOffset.UtcNow
                    }, ct);
                }
                finally { semaphore.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    // ── Per-record processing ─────────────────────────────────────────────────

    private async Task IndexRecordAsync(
        IDataSourceConnector connector,
        SourceRecord         record,
        CancellationToken    ct)
    {
        // Skip unchanged records
        var existing = await _stateStore.GetAsync(record.Id, record.DataSourceName, ct);
        if (existing?.Status == IndexingStatus.Indexed
            && existing.LastIndexed >= record.LastModified)
        {
            _logger.LogDebug("[{Sys}/{Src}] Skipping unchanged '{Id}'",
                _system.Name, record.DataSourceName, record.Id);
            return;
        }

        // Extract text from binary content if needed
        var text = record.Content;
        if (string.IsNullOrWhiteSpace(text) && record.RawContent != null)
            text = await _extractor.ExtractAsync(record.RawContent, record.MimeType, record.Title, ct);

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

        _logger.LogDebug("[{Sys}/{Src}] Indexing '{Title}' ({Len} chars)",
            _system.Name, record.DataSourceName, record.Title, text.Length);

        var textChunks = _chunker.Chunk(text);
        var embeddings = await _embedder.EmbedBatchAsync(textChunks, ct);

        var enrichedMeta = new Dictionary<string, string>(record.Metadata)
        {
            ["ConnectorType"] = connector.ConnectorType.ToString()
        };

        var chunks = textChunks.Select((t, i) => new DocumentChunk
        {
            SourceId       = record.Id,
            DataSourceName = record.DataSourceName,
            Title          = record.Title,
            Url            = record.Url,
            Author         = record.Author,
            LastModified   = record.LastModified,
            Content        = t,
            ChunkIndex     = i,
            TotalChunks    = textChunks.Count,
            Metadata       = enrichedMeta,
            Embedding      = embeddings[i]
        }).ToList();

        await _vectorStore.DeleteBySourceIdAsync(record.Id, record.DataSourceName, ct);
        await _vectorStore.UpsertAsync(chunks, ct);

        await _stateStore.SaveAsync(new IndexingRecord
        {
            SourceId       = record.Id,
            DataSourceName = record.DataSourceName,
            RagSystemName  = _system.Name,
            Title          = record.Title,
            Status         = IndexingStatus.Indexed,
            ChunkCount     = chunks.Count,
            LastIndexed    = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("[{Sys}/{Src}] Indexed '{Title}' → {N} chunks",
            _system.Name, record.DataSourceName, record.Title, chunks.Count);
    }
}
