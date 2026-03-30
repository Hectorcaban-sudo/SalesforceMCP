using ChatApp.Agents;
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

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Salesforce AI Chat API",
        Version     = "v1",
        Description = "SK GroupChatOrchestration — Accounts, Opportunities & Contracts agents."
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

    // Buffer that receives every agent response via the ResponseCallback
    var responseBuffer = new List<ChatMessageContent>();

    // Build orchestration fresh per request (stateful runtime)
    var orchestration = SalesforceAgentBuilder.BuildOrchestration(kernel, responseBuffer);

    // Start the in-process runtime
    var runtime = new InProcessRuntime();
    await runtime.StartAsync(cancellationToken);

    try
    {
        // Pass the last user message as the task — history context is embedded in the prompt
        var userMessage = messages.Last().Content!;

        // Include prior history as context prefix in the task string
        var context = string.Join("\n", messages.SkipLast(1)
            .Select(m => $"{m.Role}: {m.Content}"));

        var task = string.IsNullOrWhiteSpace(context)
            ? userMessage
            : $"Conversation so far:\n{context}\n\nLatest message: {userMessage}";

        // Invoke the GroupChatOrchestration
        var orchestrationResult = await orchestration.InvokeAsync(task, runtime, cancellationToken);

        // Wait for the final summarised result (from FilterResults)
        var finalText = await orchestrationResult.GetValueAsync(
            TimeSpan.FromSeconds(120), cancellationToken);

        // Map buffered responses → AgentResponseDto
        var agentResponses = responseBuffer
            .Where(m => m.Role == AuthorRole.Assistant)
            .Select(m => new AgentResponseDto
            {
                AgentName = m.AuthorName ?? "Agent",
                Content   = m.Content    ?? string.Empty,
            })
            .ToList();

        // If nothing was captured, fall back to the final text
        if (agentResponses.Count == 0)
            agentResponses.Add(new AgentResponseDto { AgentName = "Agent", Content = finalText });

        // Build a single assistant entry for history (all agent replies combined)
        var combinedReply = string.Join("\n\n",
            agentResponses.Select(r => $"[{r.AgentName}] {r.Content}"));

        return Results.Ok(new AgentChatResponseDto
        {
            Responses      = agentResponses,
            UpdatedHistory = Append(messages, "assistant", combinedReply),
        });
    }
    finally
    {
        // Always stop the runtime to free resources
        await runtime.RunUntilIdleAsync();
    }
})
.WithName("AgentChat")
.WithSummary("GroupChatOrchestration — routes to Accounts, Opportunities, or Contracts agent");

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

public record AgentResponseDto
{
    public string AgentName { get; init; } = string.Empty;
    public string Content   { get; init; } = string.Empty;
}

public record AgentChatResponseDto
{
    public List<AgentResponseDto>  Responses      { get; init; } = [];
    public List<ChatMessageDto>    UpdatedHistory { get; init; } = [];
}
