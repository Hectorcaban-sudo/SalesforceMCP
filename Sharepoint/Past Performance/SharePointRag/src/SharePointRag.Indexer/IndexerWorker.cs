using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Indexer;

/// <summary>
/// Background worker that runs the indexing pipeline on a schedule:
///   • Full index on first start (or if index is empty)
///   • Delta index every DeltaIntervalMinutes thereafter
///   • Full index every FullIndexIntervalHours thereafter
///
/// Run this as a standalone Worker Service or Azure Container App Job.
/// </summary>
public sealed class IndexerWorker(
    IIndexingPipeline pipeline,
    IVectorStore vectorStore,
    IOptions<IndexerOptions> opts,
    ILogger<IndexerWorker> logger) : BackgroundService
{
    private readonly IndexerOptions _opts = opts.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexer worker starting…");

        // Ensure the index exists before first run
        await vectorStore.CreateIndexIfNotExistsAsync(stoppingToken);

        bool firstRun = true;
        DateTimeOffset lastFullIndex = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool doFull = firstRun
                    || (DateTimeOffset.UtcNow - lastFullIndex).TotalHours >= _opts.FullIndexIntervalHours;

                if (doFull)
                {
                    logger.LogInformation("Starting full index…");
                    await pipeline.RunFullIndexAsync(stoppingToken);
                    lastFullIndex = DateTimeOffset.UtcNow;
                    firstRun = false;
                    logger.LogInformation("Full index complete. Next full in {H}h.", _opts.FullIndexIntervalHours);
                }
                else
                {
                    logger.LogInformation("Starting delta index…");
                    await pipeline.RunDeltaIndexAsync(stoppingToken);
                    logger.LogInformation("Delta index complete. Next delta in {M}m.", _opts.DeltaIntervalMinutes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Indexing run failed. Will retry after interval.");
            }

            await Task.Delay(
                TimeSpan.FromMinutes(_opts.DeltaIntervalMinutes),
                stoppingToken);
        }

        logger.LogInformation("Indexer worker stopped.");
    }
}

public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    /// <summary>How often to run a delta (incremental) index in minutes.</summary>
    public int DeltaIntervalMinutes { get; set; } = 30;

    /// <summary>How often to run a full re-index in hours.</summary>
    public int FullIndexIntervalHours { get; set; } = 24;
}
