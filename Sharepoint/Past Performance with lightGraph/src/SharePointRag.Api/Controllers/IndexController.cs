using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// Multi-source, multi-system index management controller.
///
/// All write endpoints accept an optional ?system= query parameter.
/// Omit it to target ALL configured systems.
///
/// New endpoints vs previous version:
///   GET  /api/index/registry              → full definition of all systems + data sources
///   POST /api/index/test-connections      → test all data source connections (or ?system=X)
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
    /// Runtime status of all RAG systems: data source reachability, indexed record
    /// counts, and last-index timestamps. Filter with ?system=Name.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<RegistryStatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatusAsync(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var all      = await registry.GetAllStatusAsync(ct);
        var filtered = system is not null
            ? all.Where(s => s.SystemName.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList()
            : all;

        return Ok(new RegistryStatusResponse(
            Systems:              filtered,
            AvailableSystemNames: registry.SystemNames,
            AvailableDataSources: registry.DataSourceNames));
    }

    // ── Registry definition ───────────────────────────────────────────────────

    /// <summary>
    /// Full static definition of all configured RAG systems and data sources.
    /// Useful for UI introspection and debugging.
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
                sys.DataSourceNames,
                sys.TopK,
                sys.MinScore);
        }).ToList();

        var dataSources = registry.DataSourceNames.Select(name =>
        {
            var ds = registry.GetDataSource(name);
            // Mask credentials before exposing via REST
            var safeProps = ds.Properties
                .Where(kv => !IsSensitiveKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            return new DataSourceDefinitionDto(
                ds.Name,
                ds.Type.ToString(),
                safeProps,
                ds.CrawlParallelism,
                ds.DeltaSupported);
        }).ToList();

        return Ok(new RegistryDefinitionResponse(systems, dataSources));
    }

    // ── Connection testing ────────────────────────────────────────────────────

    /// <summary>
    /// Test connectivity to all data sources in the specified system(s).
    /// Calls IDataSourceConnector.TestConnectionAsync() for each.
    /// </summary>
    [HttpPost("test-connections")]
    [ProducesResponseType<List<ConnectionTestResult>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestConnectionsAsync(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var systemNames = ResolveSystemNames(system);
        var results     = new List<ConnectionTestResult>();

        // Collect unique data source names across all target systems
        var dsNames = systemNames
            .SelectMany(n => registry.GetSystem(n).DataSourceNames)
            .Distinct()
            .ToList();

        foreach (var dsName in dsNames)
        {
            var ds        = registry.GetDataSource(dsName);
            var connector = registry.GetConnector(dsName);

            string message;
            bool   ok;
            try
            {
                message = await connector.TestConnectionAsync(ct);
                ok      = !message.StartsWith("Error:") &&
                          !message.StartsWith("Connection failed:");
            }
            catch (Exception ex)
            {
                message = $"Exception: {ex.Message}";
                ok      = false;
            }

            results.Add(new ConnectionTestResult(
                DataSourceName: dsName,
                ConnectorType:  ds.Type.ToString(),
                IsReachable:    ok,
                Message:        message));
        }

        return Ok(results);
    }

    // ── Provision ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure the LiteGraph graph schema exists for the specified system(s).
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

        return Ok(new { provisioned = names, message = "Schema + HNSW index provisioned." });
    }

    // ── Full index ────────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a full re-index of all data sources assigned to the specified system(s).
    /// Long-running — returns 202 immediately. Monitor logs for progress.
    /// Supports SharePoint, SQL, Excel, Deltek, and Custom sources.
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
                try   { await registry.GetPipeline(name).RunFullIndexAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "Full index failed: '{S}'", name); }
        }, ct);

        return Accepted(new { message = "Full index started.", systems = names });
    }

    // ── Delta index ───────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger an incremental re-index (records modified since last full run).
    /// Sources with DeltaSupported=false fall back to a full re-index automatically.
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
                try   { await registry.GetPipeline(name).RunDeltaIndexAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "Delta index failed: '{S}'", name); }
        }, ct);

        return Accepted(new { message = "Delta index started.", systems = names });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<string> ResolveSystemNames(string? system) =>
        system is not null ? [system] : [.. registry.SystemNames];

    private static bool IsSensitiveKey(string key) =>
        key.Contains("Secret",   StringComparison.OrdinalIgnoreCase) ||
        key.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("ApiKey",   StringComparison.OrdinalIgnoreCase) ||
        key.Contains("Token",    StringComparison.OrdinalIgnoreCase) ||
        key.Contains("Key",      StringComparison.OrdinalIgnoreCase);
}

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record RegistryStatusResponse(
    List<RagSystemStatus> Systems,
    IReadOnlyList<string> AvailableSystemNames,
    IReadOnlyList<string> AvailableDataSources
);

public record RegistryDefinitionResponse(
    List<SystemDefinitionDto> Systems,
    List<DataSourceDefinitionDto> DataSources
);

public record SystemDefinitionDto(
    string       Name,
    string       Description,
    List<string> DataSourceNames,
    int          TopK,
    double       MinScore
);

public record DataSourceDefinitionDto(
    string                      Name,
    string                      ConnectorType,
    Dictionary<string, string>  Properties,         // sensitive keys masked
    int                         CrawlParallelism,
    bool                        DeltaSupported
);

public record ConnectionTestResult(
    string DataSourceName,
    string ConnectorType,
    bool   IsReachable,
    string Message
);
