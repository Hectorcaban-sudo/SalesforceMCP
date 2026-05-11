using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Interfaces;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using System.Text;
using System.Text.Json;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// REST surface for the Past Performance Agent.
/// All endpoints work regardless of which connector types feed the PP systems —
/// SharePoint documents, SQL databases, Excel spreadsheets, Deltek, or custom.
///
/// Source filtering is available on most endpoints via:
///   ?connectorTypes=SqlDatabase,Deltek    → restrict to structured sources only
///   ?dataSources=DeltekVantagepoint       → restrict to a specific named source
/// </summary>
[ApiController]
[Route("api/pastperformance")]
[Produces("application/json")]
public sealed class PastPerformanceController(
    IPastPerformanceOrchestrator orchestrator,
    ILibraryRegistry registry,
    ILogger<PastPerformanceController> logger) : ControllerBase
{
    // ── General question ──────────────────────────────────────────────────────

    /// <summary>
    /// Ask any past-performance question. The agent detects intent automatically
    /// and searches across all configured data sources.
    /// </summary>
    [HttpPost("ask")]
    [ProducesResponseType<PastPerformanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AskAsync(
        [FromBody]  AskPastPerformanceRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        logger.LogInformation("[PP] REST ask: {Q}", request.Question);
        var response = await orchestrator.HandleAsync(request.Question, ct);
        return Ok(response);
    }

    // ── Volume draft ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a complete Past Performance Volume.
    /// Searches SharePoint, SQL, Deltek, Excel, and any other configured sources.
    /// </summary>
    [HttpPost("volume")]
    [ProducesResponseType<PastPerformanceVolumeSection>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateVolumeAsync(
        [FromBody] GenerateVolumeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SolicitationContext))
            return BadRequest(new { error = "SolicitationContext must not be empty." });

        var question = $"Draft a complete past performance volume for solicitation: {request.SolicitationContext}";
        logger.LogInformation("[PP] REST volume: {S}", request.SolicitationContext);

        var response = await orchestrator.HandleAsync(question, ct);

        if (response.DraftedSection is null)
            return Ok(new
            {
                message            = "No volume generated — check that data sources are indexed.",
                DataSourcesSearched = response.DataSourcesSearched,
                response
            });

        return Ok(response.DraftedSection);
    }

    /// <summary>Download the drafted volume as plain text.</summary>
    [HttpPost("volume/download")]
    [Produces("text/plain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadVolumeAsync(
        [FromBody] GenerateVolumeRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SolicitationContext))
            return BadRequest(new { error = "SolicitationContext must not be empty." });

        var question = $"Draft a complete past performance volume for solicitation: {request.SolicitationContext}";
        var response = await orchestrator.HandleAsync(question, ct);

        if (response.DraftedSection is null)
            return NotFound("No volume generated.");

        var text = RenderVolumeAsText(response.DraftedSection, response.DataSourcesSearched);
        return File(Encoding.UTF8.GetBytes(text), "text/plain",
            $"PastPerformanceVolume_{DateTime.UtcNow:yyyyMMdd_HHmm}.txt");
    }

    // ── Contract search ───────────────────────────────────────────────────────

    /// <summary>
    /// Find contracts relevant to a SOW across all data sources.
    /// Use connectorTypes/dataSources filters to restrict to specific sources.
    /// </summary>
    [HttpPost("contracts/search")]
    [ProducesResponseType<ContractSearchResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchContractsAsync(
        [FromBody]  ContractSearchRequest request,
        CancellationToken ct)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SowDescription))
            parts.Add($"Find contracts similar to this SOW: {request.SowDescription}");
        if (!string.IsNullOrWhiteSpace(request.Keywords))
            parts.Add($"Keywords: {request.Keywords}");
        if (request.ConnectorTypes?.Any() == true)
            parts.Add($"Restrict to connector types: {string.Join(", ", request.ConnectorTypes)}");
        if (request.DataSources?.Any() == true)
            parts.Add($"Restrict to data sources: {string.Join(", ", request.DataSources)}");

        var question = string.Join(". ", parts);
        var response = await orchestrator.HandleAsync(question, ct);

        return Ok(new ContractSearchResponse(
            response.RelevantContracts,
            response.Citations,
            response.Warnings,
            response.DataSourcesSearched));
    }

    // ── CPARS ratings ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extract CPARS ratings across all indexed sources.
    /// Structured sources (SQL, Deltek) that carry rating fields are included automatically.
    /// </summary>
    [HttpGet("cpars")]
    [ProducesResponseType<PastPerformanceResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCparsRatingsAsync(
        [FromQuery] string? agency,
        [FromQuery] List<string>? connectorTypes,
        CancellationToken ct)
    {
        var parts = new List<string>();
        parts.Add(agency is not null
            ? $"What are our CPARS ratings for {agency} contracts?"
            : "Show all CPARS ratings across our entire portfolio.");
        if (connectorTypes?.Any() == true)
            parts.Add($"Restrict to connector types: {string.Join(", ", connectorTypes)}");

        var response = await orchestrator.HandleAsync(string.Join(" ", parts), ct);
        return Ok(response);
    }

    // ── Gap analysis ──────────────────────────────────────────────────────────

    /// <summary>Identify past performance gaps vs solicitation requirements.</summary>
    [HttpPost("gaps")]
    [ProducesResponseType<PastPerformanceResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AnalyseGapsAsync(
        [FromBody] GapAnalysisRequest request,
        CancellationToken ct)
    {
        var question = $"Identify past performance gaps for: {request.Requirements}";
        var response = await orchestrator.HandleAsync(question, ct);
        return Ok(response);
    }

    // ── Data sources introspection ────────────────────────────────────────────

    /// <summary>
    /// List all data sources feeding the Past Performance systems, with their connector types.
    /// Useful for understanding which sources are available for filtering.
    /// </summary>
    [HttpGet("sources")]
    [ProducesResponseType<List<PpDataSourceInfo>>(StatusCodes.Status200OK)]
    public IActionResult GetDataSourcesAsync()
    {
        // Reflect which data sources feed PP systems from the registry
        var ppSystems = registry.SystemNames
            .Where(name =>
            {
                try { var sys = registry.GetSystem(name); return true; }
                catch { return false; }
            })
            .Select(name => registry.GetSystem(name))
            .ToList();

        var sources = ppSystems
            .SelectMany(sys => sys.DataSourceNames)
            .Distinct()
            .Select(dsName =>
            {
                var ds = registry.GetDataSource(dsName);
                return new PpDataSourceInfo(
                    DataSourceName: dsName,
                    ConnectorType:  ds.Type.ToString(),
                    DeltaSupported: ds.DeltaSupported,
                    Systems:        ppSystems
                                        .Where(s => s.DataSourceNames.Contains(dsName))
                                        .Select(s => s.Name)
                                        .ToList());
            })
            .ToList();

        return Ok(sources);
    }

    // ── Volume text renderer ──────────────────────────────────────────────────

    private static string RenderVolumeAsText(
        PastPerformanceVolumeSection volume,
        List<string> dataSources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PAST PERFORMANCE VOLUME");
        sb.AppendLine($"Solicitation: {volume.SolicitationReference}");
        sb.AppendLine($"Generated: {volume.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Data Sources: {string.Join(", ", dataSources)}");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine().AppendLine("EXECUTIVE SUMMARY").AppendLine(new string('-', 40));
        sb.AppendLine(volume.ExecutiveSummary).AppendLine();

        for (int i = 0; i < volume.Narratives.Count; i++)
        {
            var n = volume.Narratives[i];
            sb.AppendLine(new string('=', 72));
            sb.AppendLine($"CONTRACT {i + 1}: {n.Contract.ContractNumber}");
            sb.AppendLine($"Source:  {n.Contract.DataSourceName} ({n.Contract.ConnectorType})");
            sb.AppendLine($"Agency:  {n.Contract.AgencyName}");
            sb.AppendLine($"Title:   {n.Contract.Title}");
            sb.AppendLine($"Value:   ${n.Contract.FinalObligatedValue ?? n.Contract.ContractValue:N0}");
            sb.AppendLine($"Period:  {n.Contract.StartDate} – {(n.Contract.IsOngoing ? "Ongoing" : n.Contract.EndDate?.ToString())}");
            if (!string.IsNullOrEmpty(n.Contract.CPARSRatingOverall))
                sb.AppendLine($"CPARS:   {n.Contract.CPARSRatingOverall}");
            sb.AppendLine().AppendLine("NARRATIVE").AppendLine(new string('-', 40));
            sb.AppendLine(n.NarrativeText).AppendLine();
            sb.AppendLine("REFERENCES").AppendLine(new string('-', 40));
            sb.AppendLine(n.ReferenceBlock).AppendLine();
        }

        if (volume.FlaggedGaps.Count > 0)
        {
            sb.AppendLine(new string('=', 72));
            sb.AppendLine("GAPS & ATTENTION ITEMS").AppendLine(new string('-', 40));
            foreach (var g in volume.FlaggedGaps) sb.AppendLine($"• {g}");
        }

        return sb.ToString();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record AskPastPerformanceRequest(string Question);

public record GenerateVolumeRequest(
    string SolicitationContext,
    int MaxContracts = 5
);

public record ContractSearchRequest(
    string? SowDescription  = null,
    string? Keywords        = null,
    string? Agency          = null,
    string? NaicsCode       = null,
    int     TopK            = 5,
    /// <summary>e.g. ["SqlDatabase","Deltek"] to restrict to structured sources.</summary>
    List<string>? ConnectorTypes = null,
    /// <summary>e.g. ["DeltekVantagepoint"] to restrict to specific named sources.</summary>
    List<string>? DataSources   = null
);

public record ContractSearchResponse(
    IReadOnlyList<ContractRecord> Contracts,
    List<string>                  Citations,
    List<string>                  Warnings,
    List<string>                  DataSourcesSearched
);

public record GapAnalysisRequest(string Requirements);

public record PpDataSourceInfo(
    string       DataSourceName,
    string       ConnectorType,
    bool         DeltaSupported,
    List<string> Systems
);
