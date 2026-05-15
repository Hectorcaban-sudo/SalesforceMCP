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

    // ── Per-source store listing ─────────────────────────────────────────────────

    /// <summary>
    /// List all per-source vector stores with their read/write mode and metadata schema summary.
    /// Filter by ?system= to see stores for a specific system only.
    /// </summary>
    [HttpGet("stores")]
    [ProducesResponseType<List<SourceStoreInfo>>(StatusCodes.Status200OK)]
    public IActionResult GetStores([FromQuery] string? system)
    {
        var result = new List<SourceStoreInfo>();

        var systems = system is not null
            ? registry.SystemNames.Where(n => n.Equals(system, StringComparison.OrdinalIgnoreCase))
            : registry.SystemNames;

        foreach (var sysName in systems)
        {
            var sys = registry.GetSystem(sysName);
            foreach (var dsName in sys.DataSourceNames)
            {
                var ds = registry.GetDataSource(dsName);
                result.Add(new SourceStoreInfo(
                    SystemName:      sysName,
                    DataSourceName:  dsName,
                    ConnectorType:   ds.Type.ToString(),
                    StoreName:       $"{sysName}__{dsName}",
                    ReadOnly:        ds.ReadOnly,
                    SchemaFieldCount: ds.MetadataSchema.Count
                ));
            }
        }

        return Ok(result);
    }

    // ── Metadata schema ───────────────────────────────────────────────────────

    /// <summary>
    /// Return the declared metadata schema for a specific data source.
    /// The schema documents which fields appear in DocumentChunk.Metadata,
    /// their types, allowed values, and descriptions.
    /// Especially useful for read-only sources where no connector can be introspected.
    /// </summary>
    [HttpGet("schema/{dataSourceName}")]
    [ProducesResponseType<DataSourceSchemaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSchema(string dataSourceName)
    {
        try
        {
            var ds = registry.GetDataSource(dataSourceName);
            var fields = ds.MetadataSchema.ToDictionary(
                kv => kv.Key,
                kv => new MetadataFieldDto(
                    kv.Value.Type,
                    kv.Value.Description,
                    kv.Value.AllowedValues,
                    kv.Value.Examples,
                    kv.Value.Required,
                    kv.Value.Searchable));

            return Ok(new DataSourceSchemaResponse(
                DataSourceName: dataSourceName,
                ConnectorType:  ds.Type.ToString(),
                ReadOnly:       ds.ReadOnly,
                Fields:         fields,
                Message:        fields.Count == 0
                    ? "No schema declared. Add MetadataSchema to this data source in appsettings."
                    : $"{fields.Count} field(s) declared."
            ));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Data source '{dataSourceName}' not found." });
        }
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

            var schemaDto = ds.MetadataSchema.ToDictionary(
                kv => kv.Key,
                kv => new MetadataFieldDto(
                    kv.Value.Type,
                    kv.Value.Description,
                    kv.Value.AllowedValues,
                    kv.Value.Examples,
                    kv.Value.Required,
                    kv.Value.Searchable));

            return new DataSourceDefinitionDto(
                ds.Name,
                ds.Type.ToString(),
                safeProps,
                ds.CrawlParallelism,
                ds.DeltaSupported,
                ds.ReadOnly,
                schemaDto);
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
    /// Trigger a full re-index of all WRITABLE data sources in the specified system(s).
    /// Read-only sources are skipped — they are indexed by another system.
    /// Long-running — returns 202 immediately.
    /// </summary>
    [HttpPost("full")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerFullIndex(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var names         = ResolveSystemNames(system);
        var readOnlyWarns = GetReadOnlyWarnings(names);

        logger.LogInformation("Full index triggered for: [{S}]", string.Join(", ", names));

        _ = Task.Run(async () =>
        {
            foreach (var name in names)
                try   { await registry.GetPipeline(name).RunFullIndexAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "Full index failed: '{S}'", name); }
        }, ct);

        return Accepted(new
        {
            message  = "Full index started (read-only sources skipped).",
            systems  = names,
            warnings = readOnlyWarns
        });
    }

    // ── Delta index ───────────────────────────────────────────────────────────

    /// <summary>
    /// Trigger an incremental re-index of WRITABLE sources (modified since last full run).
    /// Read-only sources are skipped. Sources with DeltaSupported=false fall back to full.
    /// </summary>
    [HttpPost("delta")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerDeltaIndex(
        [FromQuery] string? system,
        CancellationToken ct)
    {
        var names         = ResolveSystemNames(system);
        var readOnlyWarns = GetReadOnlyWarnings(names);

        logger.LogInformation("Delta index triggered for: [{S}]", string.Join(", ", names));

        _ = Task.Run(async () =>
        {
            foreach (var name in names)
                try   { await registry.GetPipeline(name).RunDeltaIndexAsync(ct); }
                catch (Exception ex) { logger.LogError(ex, "Delta index failed: '{S}'", name); }
        }, ct);

        return Accepted(new
        {
            message  = "Delta index started (read-only sources skipped).",
            systems  = names,
            warnings = readOnlyWarns
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<string> ResolveSystemNames(string? system) =>
        system is not null ? [system] : [.. registry.SystemNames];

    /// <summary>Collect human-readable warnings for any read-only sources in the given systems.</summary>
    private List<string> GetReadOnlyWarnings(IEnumerable<string> systemNames)
    {
        var warnings = new List<string>();
        foreach (var sysName in systemNames)
        {
            try
            {
                var sys = registry.GetSystem(sysName);
                var readOnly = sys.DataSourceNames
                    .Where(n => registry.GetDataSource(n).ReadOnly)
                    .ToList();
                foreach (var ds in readOnly)
                    warnings.Add($"[{sysName}] '{ds}' is ReadOnly — skipped (indexed externally).");
            }
            catch { /* system not found — pipeline will handle */ }
        }
        return warnings;
    }

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
    string                                Name,
    string                                ConnectorType,
    Dictionary<string, string>            Properties,     // sensitive keys masked
    int                                   CrawlParallelism,
    bool                                  DeltaSupported,
    bool                                  ReadOnly,
    Dictionary<string, MetadataFieldDto>  MetadataSchema
);

/// <summary>Describes a single metadata field declared on a data source.</summary>
public record MetadataFieldDto(
    string       Type,
    string       Description,
    List<string> AllowedValues,
    List<string> Examples,
    bool         Required,
    bool         Searchable
);

public record ConnectionTestResult(
    string DataSourceName,
    string ConnectorType,
    bool   IsReachable,
    string Message
);
