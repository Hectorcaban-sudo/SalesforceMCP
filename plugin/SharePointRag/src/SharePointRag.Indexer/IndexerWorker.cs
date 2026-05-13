using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Indexer;

/// <summary>
/// Background worker that runs full + delta indexing on a schedule.
///
/// By default indexes ALL configured systems.
/// Set Indexer:SystemFilter to a non-empty list to restrict to specific systems:
///
///   "Indexer": {
///     "SystemFilter": ["PastPerformance", "ProposalArchive"]
///   }
///
/// Run multiple instances with different SystemFilter values to parallelise
/// indexing across machines or containers.
/// </summary>
public sealed class IndexerWorker(
    ILibraryRegistry registry,
    IOptions<IndexerOptions> opts,
    ILogger<IndexerWorker> logger) : BackgroundService
{
    private readonly IndexerOptions _opts = opts.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Determine which systems to index
        var systems = _opts.SystemFilter is { Count: > 0 }
            ? _opts.SystemFilter
            : [.. registry.SystemNames];

        logger.LogInformation(
            "Indexer worker starting. Target systems: [{S}]",
            string.Join(", ", systems));

        // Validate all systems exist before entering the loop
        foreach (var name in systems)
            registry.GetSystem(name); // throws KeyNotFoundException if misconfigured

        // Provision all target systems upfront
        foreach (var name in systems)
        {
            await registry.GetVectorStore(name).CreateIndexIfNotExistsAsync(stoppingToken);
            logger.LogInformation("[{S}] Schema provisioned.", name);
        }

        bool firstRun = true;
        var lastFullIndex = new Dictionary<string, DateTimeOffset>(
            systems.ToDictionary(s => s, _ => DateTimeOffset.MinValue));

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var name in systems)
            {
                if (stoppingToken.IsCancellationRequested) break;

                bool doFull = firstRun
                    || (DateTimeOffset.UtcNow - lastFullIndex[name]).TotalHours
                        >= _opts.FullIndexIntervalHours;

                try
                {
                    var pipeline = registry.GetPipeline(name);

                    if (doFull)
                    {
                        logger.LogInformation("[{S}] Starting full index…", name);
                        await pipeline.RunFullIndexAsync(stoppingToken);
                        lastFullIndex[name] = DateTimeOffset.UtcNow;
                        logger.LogInformation(
                            "[{S}] Full index complete. Next full in {H}h.",
                            name, _opts.FullIndexIntervalHours);
                    }
                    else
                    {
                        logger.LogInformation("[{S}] Starting delta index…", name);
                        await pipeline.RunDeltaIndexAsync(stoppingToken);
                        logger.LogInformation(
                            "[{S}] Delta index complete. Next delta in {M}m.",
                            name, _opts.DeltaIntervalMinutes);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[{S}] Indexing run failed. Will retry after interval.", name);
                }
            }

            firstRun = false;

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

    /// <summary>How often to run delta indexing (minutes). Default 30.</summary>
    public int DeltaIntervalMinutes { get; set; } = 30;

    /// <summary>How often to run a full re-index (hours). Default 24.</summary>
    public int FullIndexIntervalHours { get; set; } = 24;

    /// <summary>
    /// Restrict this worker to a subset of configured systems.
    /// Empty = all systems. Useful for sharding across multiple containers.
    /// </summary>
    public List<string> SystemFilter { get; set; } = [];
}
