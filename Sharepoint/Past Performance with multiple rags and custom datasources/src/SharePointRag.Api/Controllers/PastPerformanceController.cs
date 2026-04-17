using Microsoft.AspNetCore.Mvc;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using System.Text;
using System.Text.Json;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// REST surface for the Past Performance Agent.
/// Suitable for integration into proposal automation workflows,
/// CI/CD pipeline checks, or custom front-end capture tools.
/// </summary>
[ApiController]
[Route("api/pastperformance")]
[Produces("application/json")]
public sealed class PastPerformanceController(
    IPastPerformanceOrchestrator orchestrator,
    ILogger<PastPerformanceController> logger) : ControllerBase
{
    // ── General question ──────────────────────────────────────────────────────

    /// <summary>
    /// Ask any past-performance question. The agent detects intent automatically.
    /// </summary>
    [HttpPost("ask")]
    [ProducesResponseType<PastPerformanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AskAsync(
        [FromBody] AskPastPerformanceRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        logger.LogInformation("PP REST ask: {Q}", request.Question);
        var response = await orchestrator.HandleAsync(request.Question, ct);
        return Ok(response);
    }

    // ── Volume draft ──────────────────────────────────────────────────────────

    /// <summary>
    /// Generate a complete Past Performance Volume for a specific solicitation.
    /// Returns the full section with narrative text and reference blocks.
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
        logger.LogInformation("PP REST volume: {S}", request.SolicitationContext);

        var response = await orchestrator.HandleAsync(question, ct);

        if (response.DraftedSection is null)
            return Ok(new { message = "No volume was generated — check that documents are indexed.", response });

        return Ok(response.DraftedSection);
    }

    /// <summary>
    /// Download the drafted volume as a plain-text document.
    /// </summary>
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
            return NotFound("No volume was generated.");

        var text = RenderVolumeAsText(response.DraftedSection);
        return File(Encoding.UTF8.GetBytes(text), "text/plain",
            $"PastPerformanceVolume_{DateTime.UtcNow:yyyyMMdd_HHmm}.txt");
    }

    // ── Contract search ───────────────────────────────────────────────────────

    /// <summary>
    /// Find contracts relevant to a SOW or opportunity description.
    /// Returns structured contract records with relevance scores.
    /// </summary>
    [HttpPost("contracts/search")]
    [ProducesResponseType<ContractSearchResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchContractsAsync(
        [FromBody] ContractSearchRequest request,
        CancellationToken ct)
    {
        var question = string.IsNullOrWhiteSpace(request.SowDescription)
            ? request.Keywords
            : $"Find contracts similar to this SOW: {request.SowDescription}";

        var response = await orchestrator.HandleAsync(question!, ct);

        return Ok(new ContractSearchResponse(
            response.RelevantContracts,
            response.Citations,
            response.Warnings));
    }

    // ── CPARS ratings ─────────────────────────────────────────────────────────

    /// <summary>
    /// Extract CPARS ratings for all indexed contracts, optionally filtered by agency.
    /// </summary>
    [HttpGet("cpars")]
    [ProducesResponseType<PastPerformanceResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCparsRatingsAsync(
        [FromQuery] string? agency,
        CancellationToken ct)
    {
        var question = agency is not null
            ? $"What are our CPARS ratings for {agency} contracts?"
            : "Show me all CPARS ratings across our entire portfolio.";

        var response = await orchestrator.HandleAsync(question, ct);
        return Ok(response);
    }

    // ── Gap analysis ──────────────────────────────────────────────────────────

    /// <summary>
    /// Identify past performance gaps relative to solicitation requirements.
    /// </summary>
    [HttpPost("gaps")]
    [ProducesResponseType<PastPerformanceResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AnalyseGapsAsync(
        [FromBody] GapAnalysisRequest request,
        CancellationToken ct)
    {
        var question = $"Identify past performance gaps for these requirements: {request.Requirements}";
        var response = await orchestrator.HandleAsync(question, ct);
        return Ok(response);
    }

    // ── Volume text renderer ──────────────────────────────────────────────────

    private static string RenderVolumeAsText(PastPerformanceVolumeSection volume)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PAST PERFORMANCE VOLUME");
        sb.AppendLine($"Solicitation: {volume.SolicitationReference}");
        sb.AppendLine($"Generated: {volume.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine();
        sb.AppendLine("EXECUTIVE SUMMARY");
        sb.AppendLine(new string('-', 40));
        sb.AppendLine(volume.ExecutiveSummary);
        sb.AppendLine();

        for (int i = 0; i < volume.Narratives.Count; i++)
        {
            var n = volume.Narratives[i];
            sb.AppendLine(new string('=', 72));
            sb.AppendLine($"CONTRACT {i + 1}: {n.Contract.ContractNumber}");
            sb.AppendLine($"Agency: {n.Contract.AgencyName}");
            sb.AppendLine($"Title:  {n.Contract.Title}");
            sb.AppendLine($"Value:  ${n.Contract.FinalObligatedValue ?? n.Contract.ContractValue:N0}");
            sb.AppendLine($"Period: {n.Contract.StartDate} – {(n.Contract.IsOngoing ? "Ongoing" : n.Contract.EndDate?.ToString())}");
            if (!string.IsNullOrEmpty(n.Contract.CPARSRatingOverall))
                sb.AppendLine($"CPARS:  {n.Contract.CPARSRatingOverall}");
            sb.AppendLine();
            sb.AppendLine("NARRATIVE");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine(n.NarrativeText);
            sb.AppendLine();
            sb.AppendLine("REFERENCES");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine(n.ReferenceBlock);
            sb.AppendLine();
        }

        if (volume.FlaggedGaps.Count > 0)
        {
            sb.AppendLine(new string('=', 72));
            sb.AppendLine("GAPS & ATTENTION ITEMS");
            sb.AppendLine(new string('-', 40));
            foreach (var g in volume.FlaggedGaps) sb.AppendLine($"• {g}");
        }

        return sb.ToString();
    }
}

// ── Request / Response DTOs ───────────────────────────────────────────────────

public record AskPastPerformanceRequest(string Question);

public record GenerateVolumeRequest(
    string SolicitationContext,
    int MaxContracts = 5
);

public record ContractSearchRequest(
    string? SowDescription = null,
    string? Keywords       = null,
    string? Agency         = null,
    string? NaicsCode      = null,
    int     TopK           = 5
);

public record ContractSearchResponse(
    IReadOnlyList<ContractRecord> Contracts,
    List<string> Citations,
    List<string> Warnings
);

public record GapAnalysisRequest(string Requirements);
