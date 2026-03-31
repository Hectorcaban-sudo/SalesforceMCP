using ChatApp.Agents;
using ChatApp.Helpers;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Anthropic;

var builder = WebApplication.CreateBuilder(args);

// ── Semantic Kernel ───────────────────────────────────────────────────────────
var apiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured.");
var model = builder.Configuration["Anthropic:Model"] ?? "claude-opus-4-5";

builder.Services.AddKernel()
    .AddAnthropicChatCompletion(model, apiKey);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Salesforce AI Chat API",
        Version     = "v1",
        Description = "SK GroupChatOrchestration — Accounts, Opportunities & Contracts agents with follow-up suggestions."
    }));

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "v1"); c.RoutePrefix = "swagger"; });
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── POST /api/chat  ── plain single-agent chat ────────────────────────────────
app.MapPost("/api/chat", async (
    List<ChatMessageDto> messages,
    IChatCompletionService chatService,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });
    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
        return Results.BadRequest(new { error = "All messages must have non-empty content." });
    if (messages.Last().Role?.ToLowerInvariant() != "user")
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    var history = BuildChatHistory(messages);
    var reply   = await chatService.GetChatMessageContentAsync(history, cancellationToken: cancellationToken);

    return Results.Ok(Append(messages, "assistant", reply.Content ?? string.Empty));
})
.WithName("Chat")
.WithSummary("Plain IChatCompletionService — no agent orchestration");

// ── POST /api/agent-chat  ── GroupChatOrchestration ───────────────────────────
app.MapPost("/api/agent-chat", async (
    List<ChatMessageDto> messages,
    Kernel kernel,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });
    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
        return Results.BadRequest(new { error = "All messages must have non-empty content." });
    if (messages.Last().Role?.ToLowerInvariant() != "user")
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    var responseBuffer = new List<ChatMessageContent>();
    var orchestration  = SalesforceAgentBuilder.BuildOrchestration(kernel, responseBuffer);

    var runtime = new InProcessRuntime();
    await runtime.StartAsync(cancellationToken);

    try
    {
        // Embed prior history as context so the agent is aware of the conversation
        var context = string.Join("\n", messages.SkipLast(1)
            .Select(m => $"{m.Role}: {m.Content}"));

        var userMessage = messages.Last().Content!;

        var task = string.IsNullOrWhiteSpace(context)
            ? userMessage
            : $"Conversation so far:\n{context}\n\nLatest message: {userMessage}";

        var orchestrationResult = await orchestration.InvokeAsync(task, runtime, cancellationToken);

        await orchestrationResult.GetValueAsync(TimeSpan.FromSeconds(120), cancellationToken);

        // ── Parse each agent response into answer + suggestions ───────────────
        var agentResponses = responseBuffer
            .Where(m => m.Role == AuthorRole.Assistant)
            .Select(m =>
            {
                var parsed = AgentResponseParser.Parse(m.Content ?? string.Empty);
                return new AgentResponseDto
                {
                    AgentName   = m.AuthorName ?? "Agent",
                    RawContent  = m.Content    ?? string.Empty,
                    Answer      = parsed.Answer,
                    Suggestions = parsed.Suggestions,
                };
            })
            .ToList();

        // Collect all unique suggestions across all agent responses
        var allSuggestions = agentResponses
            .SelectMany(r => r.Suggestions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        // Store only the clean answer text in the history (not the raw tagged output)
        var combinedAnswer = string.Join("\n\n",
            agentResponses.Select(r => $"[{r.AgentName}] {r.Answer}"));

        return Results.Ok(new AgentChatResponseDto
        {
            Responses       = agentResponses,
            Suggestions     = allSuggestions,
            UpdatedHistory  = Append(messages, "assistant", combinedAnswer),
        });
    }
    finally
    {
        await runtime.RunUntilIdleAsync();
    }
})
.WithName("AgentChat")
.WithSummary("GroupChatOrchestration with follow-up suggestions");

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────
static ChatHistory BuildChatHistory(List<ChatMessageDto> messages)
{
    var history = new ChatHistory();
    foreach (var msg in messages)
    {
        var role = msg.Role?.ToLowerInvariant() switch
        {
            "assistant" => AuthorRole.Assistant,
            "system"    => AuthorRole.System,
            _           => AuthorRole.User
        };
        history.AddMessage(role, msg.Content!);
    }
    return history;
}

static List<ChatMessageDto> Append(List<ChatMessageDto> history, string role, string content) =>
    [.. history, new ChatMessageDto { Role = role, Content = content }];

// ── DTOs ──────────────────────────────────────────────────────────────────────
public record ChatMessageDto
{
    public string? Role    { get; init; }
    public string? Content { get; init; }
}

/// <summary>
/// A single agent's parsed response — answer text separated from suggestions.
/// </summary>
public record AgentResponseDto
{
    /// <summary>The agent that produced this response.</summary>
    public string AgentName  { get; init; } = string.Empty;

    /// <summary>The full unmodified agent output (includes the [SUGGESTIONS] tag).</summary>
    public string RawContent { get; init; } = string.Empty;

    /// <summary>The clean answer text with the suggestions block removed.</summary>
    public string Answer     { get; init; } = string.Empty;

    /// <summary>Follow-up prompts the agent suggests the user might want to ask next.</summary>
    public List<string> Suggestions { get; init; } = [];
}

/// <summary>
/// Full response from /api/agent-chat.
/// </summary>
public record AgentChatResponseDto
{
    /// <summary>One entry per agent that replied.</summary>
    public List<AgentResponseDto>  Responses      { get; init; } = [];

    /// <summary>
    /// Aggregated follow-up suggestions across all agents — deduplicated, max 6.
    /// The client should display these as tappable chips below the reply.
    /// </summary>
    public List<string>            Suggestions    { get; init; } = [];

    /// <summary>Updated conversation history for the next request.</summary>
    public List<ChatMessageDto>    UpdatedHistory { get; init; } = [];
}
