using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class IndexController(
    IIndexingPipeline pipeline,
    IVectorStore vectorStore,
    ILogger<IndexController> logger) : ControllerBase
{
    /// <summary>
    /// Ensure the RediSearch index schema exists.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProvisionIndexAsync(CancellationToken ct)
    {
        await vectorStore.CreateIndexIfNotExistsAsync(ct);
        return Ok(new { message = "SharpCoreDB schema and HNSW index provisioned (or already exists)." });
    }

    /// <summary>Check whether the RediSearch index exists.</summary>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatusAsync(CancellationToken ct)
    {
        var exists = await vectorStore.IndexExistsAsync(ct);
        return Ok(new { indexExists = exists });
    }

    /// <summary>
    /// Trigger a full re-index of the entire SharePoint library.
    /// Long-running – returns 202 immediately; monitor via logs.
    /// </summary>
    [HttpPost("full")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerFullIndex(CancellationToken ct)
    {
        logger.LogInformation("Full index triggered via REST API");
        _ = Task.Run(async () =>
        {
            try { await pipeline.RunFullIndexAsync(ct); }
            catch (Exception ex) { logger.LogError(ex, "Full index run failed"); }
        }, ct);
        return Accepted(new { message = "Full index started. Monitor logs for progress." });
    }

    /// <summary>
    /// Trigger a delta re-index (only files modified since the last full run).
    /// </summary>
    [HttpPost("delta")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerDeltaIndex(CancellationToken ct)
    {
        logger.LogInformation("Delta index triggered via REST API");
        _ = Task.Run(async () =>
        {
            try { await pipeline.RunDeltaIndexAsync(ct); }
            catch (Exception ex) { logger.LogError(ex, "Delta index run failed"); }
        }, ct);
        return Accepted(new { message = "Delta index started. Monitor logs for progress." });
    }
}
