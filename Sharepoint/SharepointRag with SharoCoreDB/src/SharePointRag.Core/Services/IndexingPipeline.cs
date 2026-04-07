using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Threading.Channels;

namespace SharePointRag.Core.Services;

/// <summary>
/// Orchestrates full and delta indexing of the SharePoint library.
///
/// Architecture:
///   Crawler  ──► Channel<SharePointFile>  ──► N consumers that:
///                                               1. Download file
///                                               2. Extract text
///                                               3. Chunk text
///                                               4. Embed chunks (batched)
///                                               5. Upsert into vector store
///                                               6. Save index state
/// </summary>
public sealed class IndexingPipeline(
    ISharePointCrawler crawler,
    ITextExtractor extractor,
    ITextChunker chunker,
    IEmbeddingService embedder,
    IVectorStore vectorStore,
    IIndexStateStore stateStore,
    IOptions<SharePointOptions> spOpts,
    ILogger<IndexingPipeline> logger) : IIndexingPipeline
{
    private readonly SharePointOptions _opts = spOpts.Value;

    // ── Public entry points ───────────────────────────────────────────────────

    public async Task RunFullIndexAsync(CancellationToken ct = default)
    {
        await vectorStore.CreateIndexIfNotExistsAsync(ct);
        logger.LogInformation("Starting FULL index run…");
        var files = crawler.GetFilesAsync(ct);
        await ProcessFilesAsync(files, ct);
        await stateStore.SetLastFullIndexTimeAsync(DateTimeOffset.UtcNow, ct);
        logger.LogInformation("Full index run complete.");
    }

    public async Task RunDeltaIndexAsync(CancellationToken ct = default)
    {
        var since = await stateStore.GetLastFullIndexTimeAsync(ct) ?? DateTimeOffset.UtcNow.AddDays(-7);
        logger.LogInformation("Starting DELTA index run (since {Since})…", since);
        var files = crawler.GetModifiedFilesAsync(since, ct);
        await ProcessFilesAsync(files, ct);
        logger.LogInformation("Delta index run complete.");
    }

    // ── Core pipeline ─────────────────────────────────────────────────────────

    private async Task ProcessFilesAsync(
        IAsyncEnumerable<SharePointFile> files, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<SharePointFile>(
            new BoundedChannelOptions(capacity: _opts.CrawlParallelism * 4)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

        // Producer
        var producer = Task.Run(async () =>
        {
            await foreach (var f in files.WithCancellation(ct))
                await channel.Writer.WriteAsync(f, ct);
            channel.Writer.Complete();
        }, ct);

        // Consumers
        var consumers = Enumerable
            .Range(0, _opts.CrawlParallelism)
            .Select(_ => Task.Run(() => ConsumeAsync(channel.Reader, ct), ct))
            .ToArray();

        await producer;
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(ChannelReader<SharePointFile> reader, CancellationToken ct)
    {
        await foreach (var file in reader.ReadAllAsync(ct))
        {
            try
            {
                await IndexFileAsync(file, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to index {File}", file.Name);
                await stateStore.SaveAsync(new IndexingRecord
                {
                    DriveItemId  = file.DriveItemId,
                    FileName     = file.Name,
                    Status       = IndexingStatus.Failed,
                    ErrorMessage = ex.Message,
                    LastIndexed  = DateTimeOffset.UtcNow
                }, ct);
            }
        }
    }

    private async Task IndexFileAsync(SharePointFile file, CancellationToken ct)
    {
        // Skip unchanged files
        var existing = await stateStore.GetAsync(file.DriveItemId, ct);
        if (existing?.Status == IndexingStatus.Indexed
            && existing.LastIndexed >= file.LastModified)
        {
            logger.LogDebug("Skipping unchanged {File}", file.Name);
            return;
        }

        logger.LogInformation("Indexing {File} ({Size:N0} bytes)…", file.Name, file.SizeBytes);

        // 1. Download
        await using var stream = await crawler.DownloadFileAsync(file, ct);

        // 2. Extract text
        var text = await extractor.ExtractAsync(stream, file.MimeType, file.Name, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            await stateStore.SaveAsync(new IndexingRecord
            {
                DriveItemId = file.DriveItemId, FileName = file.Name,
                Status = IndexingStatus.Skipped, LastIndexed = DateTimeOffset.UtcNow
            }, ct);
            return;
        }

        // 3. Chunk
        var textChunks = chunker.Chunk(text);

        // 4. Embed all chunks (batched internally)
        var embeddings = await embedder.EmbedBatchAsync(textChunks, ct);

        // 5. Build DocumentChunks
        var docChunks = textChunks.Select((t, i) => new DocumentChunk
        {
            DriveItemId  = file.DriveItemId,
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

        // 6. Delete old chunks then upsert new ones
        await vectorStore.DeleteByDriveItemIdAsync(file.DriveItemId, ct);
        await vectorStore.UpsertAsync(docChunks, ct);

        // 7. Save state
        await stateStore.SaveAsync(new IndexingRecord
        {
            DriveItemId = file.DriveItemId,
            FileName    = file.Name,
            Status      = IndexingStatus.Indexed,
            ChunkCount  = docChunks.Count,
            LastIndexed = DateTimeOffset.UtcNow
        }, ct);

        logger.LogInformation("Indexed {File}: {Chunks} chunks", file.Name, docChunks.Count);
    }
}
