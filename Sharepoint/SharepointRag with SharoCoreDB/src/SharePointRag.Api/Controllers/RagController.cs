using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class RagController(
    IRagOrchestrator rag,
    ILogger<RagController> logger) : ControllerBase
{
    /// <summary>Ask a question and get a grounded answer with source citations.</summary>
    [HttpPost("ask")]
    [AllowAnonymous] // Secure with your own auth in production
    [ProducesResponseType<AskResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AskAsync(
        [FromBody] AskRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        logger.LogInformation("REST RAG question: {Q}", request.Question);

        var response = await rag.AskAsync(request.Question, ct);

        return Ok(new AskResponse(
            response.Question,
            response.Answer,
            response.Sources.Select(s => new SourceDto(
                s.Chunk.FileName,
                s.Chunk.WebUrl,
                s.Chunk.LibraryPath,
                s.Chunk.Author,
                s.Chunk.LastModified,
                s.Chunk.ChunkIndex,
                s.Chunk.TotalChunks,
                Math.Round(s.Score, 4)
            )).ToList()
        ));
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record AskRequest(string Question);

public record AskResponse(
    string Question,
    string Answer,
    IReadOnlyList<SourceDto> Sources
);

public record SourceDto(
    string FileName,
    string WebUrl,
    string LibraryPath,
    string? Author,
    DateTimeOffset LastModified,
    int ChunkIndex,
    int TotalChunks,
    double Score
);
