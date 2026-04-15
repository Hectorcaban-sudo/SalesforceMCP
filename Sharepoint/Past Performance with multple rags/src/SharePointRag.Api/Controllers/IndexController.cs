using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// Multi-system index management controller.
///
/// All endpoints accept an optional ?system= query parameter.
/// When omitted, the operation targets ALL configured systems.
///
/// Examples:
///   POST /api/index/provision           → provision all systems
///   POST /api/index/provision?system=HR → provision HR system only
///   POST /api/index/full?system=PastPerformance
///   GET  /api/index/status              → full registry status
/// </summary>
[ApiController]
[Route("api/index")]
[Produces("application/json")]
public sealed class IndexController(
    ILibraryRegistry registry,
    ILogger<IndexController> logger) : ControllerBase
{
    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns runtime status of all RAG systems (or a single system).
    /// Includes per-library indexed file counts and last-index timestamps.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<RegistryStatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatusAsync(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var all = await registry.GetAllStatusAsync(ct);

        var filtered = system is not null
            ? all.Where(s => s.SystemName.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList()
            : all;

        return Ok(new RegistryStatusResponse(
            Systems: filtered,
            AvailableSystemNames: registry.SystemNames,
            AvailableLibraryNames: registry.LibraryNames));
    }

    // ── Provision ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure the SharpCoreDB schema + HNSW index exists for the specified system(s).
    /// Safe to call multiple times (idempotent).
    /// </summary>
    [HttpPost("provision")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProvisionAsync(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var names = ResolveSystemNames(system);
        foreach (var name in names)
            await registry.GetVectorStore(name).CreateIndexIfNotExistsAsync(ct);

        return Ok(new { provisioned = names, message = "Schema + HNSW index provisioned (or already exists)." });
    }

    // ── Full index ────────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a full re-index of all libraries assigned to the specified system(s).
    /// Long-running — returns 202 immediately; monitor logs for progress.
    /// </summary>
    [HttpPost("full")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerFullIndex(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var names = ResolveSystemNames(system);
        logger.LogInformation("Full index triggered for: [{S}]", string.Join(", ", names));

        _ = Task.Run(async () =>
        {
            foreach (var name in names)
            {
                try { await registry.GetPipeline(name).RunFullIndexAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "Full index failed for system '{S}'", name); }
            }
        }, ct);

        return Accepted(new
        {
            message = "Full index started.",
            systems = names
        });
    }

    // ── Delta index ───────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a delta re-index (only files modified since last full run).
    /// </summary>
    [HttpPost("delta")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerDeltaIndex(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var names = ResolveSystemNames(system);
        logger.LogInformation("Delta index triggered for: [{S}]", string.Join(", ", names));

        _ = Task.Run(async () =>
        {
            foreach (var name in names)
            {
                try { await registry.GetPipeline(name).RunDeltaIndexAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "Delta index failed for system '{S}'", name); }
            }
        }, ct);

        return Accepted(new
        {
            message = "Delta index started.",
            systems = names
        });
    }

    // ── Registry introspection ────────────────────────────────────────────────

    /// <summary>
    /// List all configured RAG systems and their library assignments.
    /// </summary>
    [HttpGet("registry")]
    [ProducesResponseType<RegistryDefinitionResponse>(StatusCodes.Status200OK)]
    public IActionResult GetRegistry()
    {
        var systems = registry.SystemNames.Select(name =>
        {
            var sys = registry.GetSystem(name);
            return new SystemDefinitionDto(
                sys.Name,
                sys.Description,
                sys.LibraryNames,
                sys.TopK,
                sys.MinScore);
        }).ToList();

        var libraries = registry.LibraryNames.Select(name =>
        {
            var lib = registry.GetLibrary(name);
            return new LibraryDefinitionDto(
                lib.Name,
                lib.SiteUrl,
                lib.LibraryName,
                lib.AllowedExtensions,
                lib.MaxFileSizeMb);
        }).ToList();

        return Ok(new RegistryDefinitionResponse(systems, libraries));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<string> ResolveSystemNames(string? system) =>
        system is not null
            ? [system]
            : [.. registry.SystemNames];
}

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record RegistryStatusResponse(
    List<RagSystemStatus> Systems,
    IReadOnlyList<string> AvailableSystemNames,
    IReadOnlyList<string> AvailableLibraryNames
);

public record RegistryDefinitionResponse(
    List<SystemDefinitionDto> Systems,
    List<LibraryDefinitionDto> Libraries
);

public record SystemDefinitionDto(
    string Name,
    string Description,
    List<string> LibraryNames,
    int TopK,
    double MinScore
);

public record LibraryDefinitionDto(
    string Name,
    string SiteUrl,
    string DocumentLibrary,
    List<string> AllowedExtensions,
    int MaxFileSizeMb
);
