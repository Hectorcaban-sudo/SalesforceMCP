using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Extensions;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// General-purpose RAG query endpoint.
///
/// Accepts an optional list of system names to search.
/// When omitted, searches all configured systems (full federation).
///
/// Examples:
///   POST /api/rag/ask                                   → search all systems
///   POST /api/rag/ask?systems=General                   → single system
///   POST /api/rag/ask?systems=General&systems=HR        → two specific systems
/// </summary>
[ApiController]
[Route("api/rag")]
[Produces("application/json")]
public sealed class RagController(
    IRagOrchestratorFactory factory,
    ILibraryRegistry registry,
    ILogger<RagController> logger) : ControllerBase
{
    /// <summary>Ask a question and get a grounded answer with source citations.</summary>
    [HttpPost("ask")]
    [ProducesResponseType<AskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AskAsync(
        [FromBody]  AskRequest    request,
        [FromQuery] List<string>? systems,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        // Default to all systems when none specified
        var systemNames = (systems is { Count: > 0 } ? systems : [.. registry.SystemNames])
            .AsReadOnly();

        logger.LogInformation("REST RAG ask across [{S}]: {Q}",
            string.Join(", ", systemNames), request.Question);

        var orchestrator = factory.Create(systemNames);
        var response     = await orchestrator.AskAsync(request.Question, ct);

        return Ok(new AskResponse(
            response.Question,
            response.Answer,
            response.Sources.Select(s => new SourceDto(
                s.Chunk.FileName,
                s.Chunk.WebUrl,
                s.Chunk.LibraryName,
                s.Chunk.LibraryPath,
                s.Chunk.Author,
                s.Chunk.LastModified,
                s.Chunk.ChunkIndex,
                s.Chunk.TotalChunks,
                Math.Round(s.Score, 4)
            )).ToList(),
            SystemsQueried: [.. systemNames]
        ));
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record AskRequest(string Question);

public record AskResponse(
    string Question,
    string Answer,
    IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<string> SystemsQueried
);

public record SourceDto(
    string FileName,
    string WebUrl,
    string LibraryName,
    string LibraryPath,
    string? Author,
    DateTimeOffset LastModified,
    int ChunkIndex,
    int TotalChunks,
    double Score
);
