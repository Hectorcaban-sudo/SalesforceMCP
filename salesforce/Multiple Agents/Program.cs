using ChatApp.Agents;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Anthropic;

var builder = WebApplication.CreateBuilder(args);

// ── Semantic Kernel ───────────────────────────────────────────────────────────
var apiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured.");
var model = builder.Configuration["Anthropic:Model"] ?? "claude-opus-4-5";

// Base kernel — shared chat completion service used by all agents
builder.Services.AddKernel()
    .AddAnthropicChatCompletion(model, apiKey);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Salesforce AI Chat API",
        Version     = "v1",
        Description = "Semantic Kernel AgentGroupChat — Accounts, Opportunities & Contracts agents."
    });
});

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Chat API v1");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── POST /api/chat  ── plain single-agent chat (no orchestration) ─────────────
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

    var chatHistory = BuildChatHistory(messages);

    var reply = await chatService.GetChatMessageContentAsync(
        chatHistory, cancellationToken: cancellationToken);

    return Results.Ok(Append(messages, "assistant", reply.Content ?? string.Empty));
})
.WithName("Chat")
.WithSummary("Single-agent chat using IChatCompletionService");

// ── POST /api/agent-chat  ── AgentGroupChat orchestration ────────────────────
app.MapPost("/api/agent-chat", async (
    List<ChatMessageDto> messages,
    Kernel kernel,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });

    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
        return Results.BadRequest(new { error = "All messages must have non-empty content." });

    var lastMsg = messages.Last();
    if (lastMsg.Role?.ToLowerInvariant() != "user")
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    // Build a fresh AgentGroupChat for this request
    // (AgentGroupChat is stateful so we create one per request)
    var groupChat = SalesforceAgentBuilder.BuildGroupChat(kernel);

    // Seed the group chat with prior conversation history (excluding last user msg)
    foreach (var msg in messages.SkipLast(1))
    {
        var role = msg.Role?.ToLowerInvariant() switch
        {
            "assistant" => AuthorRole.Assistant,
            "system"    => AuthorRole.System,
            _           => AuthorRole.User
        };
        groupChat.AddChatMessage(new ChatMessageContent(role, msg.Content));
    }

    // Add the latest user message — this triggers agent selection + response
    groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, lastMsg.Content));

    // Collect agent responses
    var responses = new List<AgentResponseDto>();

    await foreach (var response in groupChat.InvokeAsync(cancellationToken))
    {
        responses.Add(new AgentResponseDto
        {
            AgentName = response.AuthorName ?? "Agent",
            Content   = response.Content    ?? string.Empty,
        });
    }

    // Flatten all agent responses into a single assistant message for the client
    var replyText = string.Join("\n\n", responses.Select(r => $"**{r.AgentName}**: {r.Content}"));

    return Results.Ok(new AgentChatResponseDto
    {
        Responses      = responses,
        UpdatedHistory = Append(messages, "assistant", replyText),
    });
})
.WithName("AgentChat")
.WithSummary("AgentGroupChat — routes to Accounts, Opportunities, or Contracts agent");

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────
static ChatHistory BuildChatHistory(List<ChatMessageDto> messages)
{
    var history = new ChatHistory();
    foreach (var msg in messages)
    {
        var role = msg.Role?.ToLowerInvariant() switch
        {
            "user"      => AuthorRole.User,
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
