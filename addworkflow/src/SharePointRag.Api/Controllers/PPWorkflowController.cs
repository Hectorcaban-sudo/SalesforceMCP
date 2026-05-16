using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace SharePointRag.Api.Controllers;

/// <summary>
/// REST endpoint for the MAF-based Past Performance Workflow.
///
/// Unlike the Bot Framework adapter endpoints (/api/messages, /api/pastperformance/messages),
/// this controller uses the standard MAF session API directly:
///   workflowAgent.CreateSessionAsync()  → AgentSession
///   workflowAgent.RunAsync(messages, session)  → AgentResponse
///
/// Session management:
///   Sessions are stored in-memory keyed by SessionId (a GUID the client echoes back).
///   Pass the returned SessionId on subsequent requests to maintain conversation context.
///   Sessions are lost on restart — for production, use AgentSession serialisation:
///     var json = await workflowAgent.SerializeSessionAsync(session);
///     var restored = await workflowAgent.DeserializeSessionAsync(json);
///   and persist the JSON to Redis / CosmosDB / SQL.
///
/// Endpoint: POST /api/pastperformance/workflow/run
/// </summary>
[ApiController]
[Route("api/pastperformance/workflow")]
[Produces("application/json")]
public sealed class PPWorkflowController : ControllerBase
{
    // In-process session store: sessionId → AgentSession
    // Replace with IDistributedCache + SerializeSessionAsync/DeserializeSessionAsync
    // in production for multi-instance deployments.
    private static readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    private readonly AIAgent                         _workflowAgent;
    private readonly ILogger<PPWorkflowController>   _logger;

    public PPWorkflowController(
        [FromKeyedServices("pp-workflow")] AIAgent workflowAgent,
        ILogger<PPWorkflowController> logger)
    {
        _workflowAgent = workflowAgent;
        _logger        = logger;
    }

    /// <summary>
    /// Run one turn of the Past Performance Workflow.
    ///
    /// On the FIRST turn, omit SessionId (or send null) — a new session is created
    /// and its ID is returned. Pass that ID on every subsequent turn to maintain
    /// the conversation context (cached contracts, draft, history).
    ///
    /// Multi-turn example:
    ///   Turn 1: POST { "question": "Find DoD cloud contracts over $10M" }
    ///           → { "sessionId": "abc123", "answer": "Found 4 contracts..." }
    ///   Turn 2: POST { "question": "Now draft the volume", "sessionId": "abc123" }
    ///           → { "sessionId": "abc123", "answer": "## Draft Volume..." }
    ///   Turn 3: POST { "question": "Focus on the Army ones", "sessionId": "abc123" }
    ///           → { "sessionId": "abc123", "answer": "Refined to 2 contracts..." }
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType<PPWorkflowResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RunAsync(
        [FromBody] PPWorkflowRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { error = "Question must not be empty." });

        _logger.LogInformation("[PPWorkflow] Session={S} Question={Q}",
            request.SessionId ?? "new", request.Question);

        // Get existing session or create a new one
        AgentSession session;
        string sessionId;

        if (!string.IsNullOrEmpty(request.SessionId)
            && _sessions.TryGetValue(request.SessionId, out var existing))
        {
            session   = existing;
            sessionId = request.SessionId;
            _logger.LogDebug("[PPWorkflow] Resuming session {S}", sessionId);
        }
        else
        {
            session   = await _workflowAgent.CreateSessionAsync(ct);
            sessionId = Guid.NewGuid().ToString("N");
            _sessions[sessionId] = session;
            _logger.LogDebug("[PPWorkflow] Created session {S}", sessionId);
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, request.Question)
        };

        AgentResponse response = await _workflowAgent.RunAsync(messages, session, ct);

        // The final message from the workflow (ResponseFormatterAgent output)
        var answer = response.Messages
            .LastOrDefault(m => m.Role == ChatRole.Assistant)
            ?.Text ?? string.Empty;

        // Map all agent messages for transparency (which agent said what)
        var agentMessages = response.Messages
            .Select(m => new WorkflowMessageDto(
                Author: m.AuthorName ?? m.Role.Value,
                Text:   m.Text       ?? string.Empty))
            .ToList();

        return Ok(new PPWorkflowResponse(
            SessionId:     sessionId,
            Answer:        answer,
            AgentMessages: agentMessages
        ));
    }

    /// <summary>
    /// Run one turn with streaming — returns Server-Sent Events as each workflow
    /// step produces output. Useful for showing progress through the 3-step pipeline.
    /// </summary>
    [HttpPost("stream")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task StreamAsync(
        [FromBody] PPWorkflowRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        AgentSession session;
        string sessionId;

        if (!string.IsNullOrEmpty(request.SessionId)
            && _sessions.TryGetValue(request.SessionId, out var existing))
        {
            session   = existing;
            sessionId = request.SessionId;
        }
        else
        {
            session   = await _workflowAgent.CreateSessionAsync(ct);
            sessionId = Guid.NewGuid().ToString("N");
            _sessions[sessionId] = session;
        }

        // Emit session ID first so the client can persist it before streaming starts
        await Response.WriteAsync($"data: {{\"event\":\"session\",\"sessionId\":\"{sessionId}\"}}\n\n", ct);

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, request.Question)
        };

        await foreach (var update in _workflowAgent.RunStreamingAsync(messages, session, ct))
        {
            if (string.IsNullOrEmpty(update.Text)) continue;

            var author = update.AuthorName ?? "agent";
            var text   = System.Text.Json.JsonSerializer.Serialize(update.Text);
            await Response.WriteAsync(
                $"data: {{\"event\":\"update\",\"author\":\"{author}\",\"text\":{text}}}\n\n", ct);
        }

        await Response.WriteAsync("data: {\"event\":\"done\"}\n\n", ct);
    }

    /// <summary>Discard a session and its cached state.</summary>
    [HttpDelete("session/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DeleteSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
            return Ok(new { message = $"Session '{sessionId}' deleted." });
        return NotFound(new { error = $"Session '{sessionId}' not found." });
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record PPWorkflowRequest(
    string Question,
    string? SessionId = null
);

public record PPWorkflowResponse(
    string SessionId,
    string Answer,
    List<WorkflowMessageDto> AgentMessages
);

public record WorkflowMessageDto(
    string Author,
    string Text
);
