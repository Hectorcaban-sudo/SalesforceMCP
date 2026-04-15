using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Threading.Channels;

namespace SharePointRag.Core.Services;

/// <summary>
/// Indexing pipeline scoped to a single named RAG system.
/// Iterates over every library assigned to the system, crawling, extracting,
/// chunking, embedding, and upserting into the system's isolated vector store.
///
/// Full index:  crawl all files in all assigned libraries → upsert all
/// Delta index: crawl only files modified since last full run per library
/// </summary>
public sealed class IndexingPipeline : IIndexingPipeline
{
    private readonly RagSystemDefinition         _system;
    private readonly IReadOnlyList<ISharePointCrawler> _crawlers;
    private readonly ITextExtractor              _extractor;
    private readonly ITextChunker                _chunker;
    private readonly IEmbeddingService           _embedder;
    private readonly IVectorStore                _vectorStore;
    private readonly IIndexStateStore            _stateStore;
    private readonly ILogger<IndexingPipeline>   _logger;

    public string SystemName => _system.Name;

    public IndexingPipeline(
        RagSystemDefinition system,
        IEnumerable<ISharePointCrawler> crawlers,
        ITextExtractor extractor,
        ITextChunker chunker,
        IEmbeddingService embedder,
        IVectorStore vectorStore,
        IIndexStateStore stateStore,
        ILogger<IndexingPipeline> logger)
    {
        _system      = system;
        _crawlers    = crawlers.ToList().AsReadOnly();
        _extractor   = extractor;
        _chunker     = chunker;
        _embedder    = embedder;
        _vectorStore = vectorStore;
        _stateStore  = stateStore;
        _logger      = logger;
    }

    public async Task RunFullIndexAsync(CancellationToken ct = default)
    {
        await _vectorStore.CreateIndexIfNotExistsAsync(ct);
        _logger.LogInformation("[{Sys}] Starting FULL index across {N} library/ies…",
            _system.Name, _crawlers.Count);

        foreach (var crawler in _crawlers)
        {
            _logger.LogInformation("[{Sys}] Full-indexing library '{Lib}'…",
                _system.Name, crawler.LibraryName);
            await ProcessFilesAsync(crawler, crawler.GetFilesAsync(ct), ct);
            await _stateStore.SetLastFullIndexTimeAsync(crawler.LibraryName, DateTimeOffset.UtcNow, ct);
        }

        _logger.LogInformation("[{Sys}] Full index complete.", _system.Name);
    }

    public async Task RunDeltaIndexAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[{Sys}] Starting DELTA index…", _system.Name);

        foreach (var crawler in _crawlers)
        {
            var since = await _stateStore.GetLastFullIndexTimeAsync(crawler.LibraryName, ct)
                        ?? DateTimeOffset.UtcNow.AddDays(-7);

            _logger.LogInformation("[{Sys}] Delta-indexing library '{Lib}' since {Since}…",
                _system.Name, crawler.LibraryName, since);

            await ProcessFilesAsync(crawler, crawler.GetModifiedFilesAsync(since, ct), ct);
        }

        _logger.LogInformation("[{Sys}] Delta index complete.", _system.Name);
    }

    private async Task ProcessFilesAsync(
        ISharePointCrawler crawler,
        IAsyncEnumerable<SharePointFile> files,
        CancellationToken ct)
    {
        var libDef = _system.LibraryNames; // just for capacity hint
        var channel = Channel.CreateBounded<SharePointFile>(
            new BoundedChannelOptions(capacity: 32)
            {
                FullMode     = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

        var lib = _system.LibraryNames
            .Select(n => n)
            .FirstOrDefault(n => n == crawler.LibraryName) ?? crawler.LibraryName;

        // Producer
        var producer = Task.Run(async () =>
        {
            await foreach (var f in files.WithCancellation(ct))
                await channel.Writer.WriteAsync(f, ct);
            channel.Writer.Complete();
        }, ct);

        // Find parallelism for this library from config (stored on crawler)
        var parallelism = 4; // default; registry injects the real value at construction
        var consumers = Enumerable
            .Range(0, parallelism)
            .Select(_ => Task.Run(() => ConsumeAsync(crawler, channel.Reader, ct), ct))
            .ToArray();

        await producer;
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(
        ISharePointCrawler crawler,
        ChannelReader<SharePointFile> reader,
        CancellationToken ct)
    {
        await foreach (var file in reader.ReadAllAsync(ct))
        {
            try { await IndexFileAsync(crawler, file, ct); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Sys}/{Lib}] Failed to index {File}",
                    _system.Name, file.LibraryName, file.Name);
                await _stateStore.SaveAsync(new IndexingRecord
                {
                    DriveItemId   = file.DriveItemId,
                    FileName      = file.Name,
                    RagSystemName = _system.Name,
                    LibraryName   = file.LibraryName,
                    Status        = IndexingStatus.Failed,
                    ErrorMessage  = ex.Message,
                    LastIndexed   = DateTimeOffset.UtcNow
                }, ct);
            }
        }
    }

    private async Task IndexFileAsync(
        ISharePointCrawler crawler, SharePointFile file, CancellationToken ct)
    {
        var existing = await _stateStore.GetAsync(file.DriveItemId, file.LibraryName, ct);
        if (existing?.Status == IndexingStatus.Indexed
            && existing.LastIndexed >= file.LastModified)
        {
            _logger.LogDebug("[{Sys}/{Lib}] Skipping unchanged {File}",
                _system.Name, file.LibraryName, file.Name);
            return;
        }

        _logger.LogInformation("[{Sys}/{Lib}] Indexing {File} ({Bytes:N0} bytes)…",
            _system.Name, file.LibraryName, file.Name, file.SizeBytes);

        await using var stream = await crawler.DownloadFileAsync(file, ct);
        var text = await _extractor.ExtractAsync(stream, file.MimeType, file.Name, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            await _stateStore.SaveAsync(new IndexingRecord
            {
                DriveItemId   = file.DriveItemId, FileName = file.Name,
                RagSystemName = _system.Name,     LibraryName = file.LibraryName,
                Status        = IndexingStatus.Skipped, LastIndexed = DateTimeOffset.UtcNow
            }, ct);
            return;
        }

        var textChunks = _chunker.Chunk(text);
        var embeddings = await _embedder.EmbedBatchAsync(textChunks, ct);

        var docChunks = textChunks.Select((t, i) => new DocumentChunk
        {
            DriveItemId  = file.DriveItemId,
            LibraryName  = file.LibraryName,
            FileName     = file.Name,
            WebUrl       = file.WebUrl,
            LibraryPath  = file.LibraryPath,
            Author       = file.Author,
            LastModified = file.LastModified,
            Content      = t,
            ChunkIndex   = i,
            TotalChunks  = textChunks.Count,
            Embedding    = embeddings[i]
        }).ToList();

        await _vectorStore.DeleteByDriveItemIdAsync(file.DriveItemId, ct);
        await _vectorStore.UpsertAsync(docChunks, ct);

        await _stateStore.SaveAsync(new IndexingRecord
        {
            DriveItemId   = file.DriveItemId,
            FileName      = file.Name,
            RagSystemName = _system.Name,
            LibraryName   = file.LibraryName,
            Status        = IndexingStatus.Indexed,
            ChunkCount    = docChunks.Count,
            LastIndexed   = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("[{Sys}/{Lib}] Indexed {File}: {N} chunks",
            _system.Name, file.LibraryName, file.Name, docChunks.Count);
    }
}
