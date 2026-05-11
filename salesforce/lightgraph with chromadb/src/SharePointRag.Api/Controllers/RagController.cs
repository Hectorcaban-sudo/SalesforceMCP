using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Extensions;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// Source-agnostic RAG query endpoint.
/// Results cite their DataSourceName so callers know whether a chunk
/// came from SharePoint, SQL, Excel, Deltek, or a custom connector.
/// </summary>
[ApiController]
[Route("api/rag")]
[Produces("application/json")]
public sealed class RagController(
    IRagOrchestratorFactory factory,
    ILibraryRegistry registry,
    ILogger<RagController> logger) : ControllerBase
{
    /// <summary>
    /// Ask a question across one or more RAG systems.
    /// Omit ?systems= to search all configured systems (full federation).
    /// </summary>
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
                Title:          s.Chunk.Title,
                Url:            s.Chunk.Url,
                DataSourceName: s.Chunk.DataSourceName,
                Author:         s.Chunk.Author,
                LastModified:   s.Chunk.LastModified,
                ChunkIndex:     s.Chunk.ChunkIndex,
                TotalChunks:    s.Chunk.TotalChunks,
                Score:          Math.Round(s.Score, 4),
                Metadata:       s.Chunk.Metadata
            )).ToList(),
            SystemsQueried: [.. systemNames]
        ));
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record AskRequest(string Question);

public record AskResponse(
    string                   Question,
    string                   Answer,
    IReadOnlyList<SourceDto> Sources,
    IReadOnlyList<string>    SystemsQueried
);

public record SourceDto(
    string                     Title,
    string                     Url,
    string                     DataSourceName,
    string?                    Author,
    DateTimeOffset             LastModified,
    int                        ChunkIndex,
    int                        TotalChunks,
    double                     Score,
    Dictionary<string, string> Metadata
);
