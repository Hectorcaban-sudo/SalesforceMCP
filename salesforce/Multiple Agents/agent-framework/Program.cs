using ChatApp.Agents;
using ChatApp.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.OpenApi.Models;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

// ── Internal OpenAPI-compatible server ────────────────────────────────────────
// The OpenAI client is pointed at your internal server's base URL.
// Set InternalAI:Endpoint and InternalAI:Model in appsettings.json.
// If your server requires an API key set InternalAI:ApiKey, otherwise use a
// placeholder (many internal servers accept any non-empty value).
var endpoint = builder.Configuration["InternalAI:Endpoint"]
    ?? throw new InvalidOperationException("InternalAI:Endpoint is not configured.");
var model    = builder.Configuration["InternalAI:Model"] ?? "gpt-4o-mini";
var apiKey   = builder.Configuration["InternalAI:ApiKey"] ?? "internal";   // placeholder if not required

// Build the OpenAI ChatClient pointed at your internal server
ChatClient openAiChatClient = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
    .GetChatClient(model);

// Register as AIAgent factory — used by the plain /api/chat endpoint
// .AsAIAgent() comes from Microsoft.Agents.AI.OpenAI
builder.Services.AddSingleton<AIAgent>(_ =>
    openAiChatClient.AsAIAgent(
        instructions: "You are a helpful Salesforce assistant.",
        name: "DefaultAgent"));

// Also register the ChatClient so the agent builder can create per-agent instances
builder.Services.AddSingleton(openAiChatClient);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Salesforce AI Chat API",
        Version     = "v1",
        Description = "Microsoft Agent Framework — GroupChat with Accounts, Opportunities & Contracts agents."
    }));

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "v1"); c.RoutePrefix = "swagger"; });
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── POST /api/chat  ── plain single-turn chat ─────────────────────────────────
app.MapPost("/api/chat", async (
    List<ChatMessageDto> messages,
    AIAgent defaultAgent,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });
    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
        return Results.BadRequest(new { error = "All messages must have non-empty content." });
    if (messages.Last().Role?.ToLowerInvariant() != "user")
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    // Convert DTOs → Microsoft.Extensions.AI ChatMessage list
    var history = messages
        .Select(m => new ChatMessage(
            m.Role!.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant : ChatRole.User,
            m.Content!))
        .ToList();

    AgentRunResponse result = await defaultAgent.RunAsync(history, cancellationToken);
    var replyText = result.Messages.LastOrDefault()?.Text ?? string.Empty;

    return Results.Ok(Append(messages, "assistant", replyText));
})
.WithName("Chat")
.WithSummary("Plain AIAgent backed by internal OpenAPI server — no orchestration");

// ── POST /api/agent-chat  ── Microsoft Agent Framework GroupChat ──────────────
app.MapPost("/api/agent-chat", async (
    List<ChatMessageDto> messages,
    ChatClient openAiClient,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });
    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
        return Results.BadRequest(new { error = "All messages must have non-empty content." });
    if (messages.Last().Role?.ToLowerInvariant() != "user")
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    // Build the GroupChat workflow using the OpenAI ChatClient directly
    Workflow workflow = SalesforceAgentBuilder.BuildGroupChatWorkflow(openAiClient);

    // Embed prior history as context in the task string
    var context = string.Join("\n", messages.SkipLast(1)
        .Select(m => $"{m.Role}: {m.Content}"));
    var userMessage = messages.Last().Content!;
    var task = string.IsNullOrWhiteSpace(context)
        ? userMessage
        : $"Conversation so far:\n{context}\n\nLatest: {userMessage}";

    var inputMessages = new List<ChatMessage> { new(ChatRole.User, task) };

    // ── Run the workflow with streaming ───────────────────────────────────────
    var agentResponses = new List<AgentResponseDto>();

    await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
        workflow, inputMessages, cancellationToken);

    await run.TrySendMessageAsync(new TurnToken(emitEvents: true), cancellationToken);

    await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken))
    {
        if (evt is AgentResponseUpdateEvent update)
        {
            var response   = update.AsResponse();
            var authorName = response.Messages.FirstOrDefault()?.AuthorName ?? "Agent";
            var rawText    = string.Concat(response.Messages.Select(m => m.Text ?? ""));

            if (!string.IsNullOrWhiteSpace(rawText))
            {
                var parsed = AgentResponseParser.Parse(rawText);
                agentResponses.Add(new AgentResponseDto
                {
                    AgentName   = authorName,
                    RawContent  = rawText,
                    Answer      = parsed.Answer,
                    Suggestions = parsed.Suggestions,
                });
            }
        }
    }

    var allSuggestions = agentResponses
        .SelectMany(r => r.Suggestions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(6)
        .ToList();

    var combinedAnswer = string.Join("\n\n",
        agentResponses.Select(r => $"[{r.AgentName}] {r.Answer}"));

    return Results.Ok(new AgentChatResponseDto
    {
        Responses      = agentResponses,
        Suggestions    = allSuggestions,
        UpdatedHistory = Append(messages, "assistant", combinedAnswer),
    });
})
.WithName("AgentChat")
.WithSummary("Microsoft Agent Framework GroupChat — internal OpenAPI server");

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────
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
    public string       AgentName   { get; init; } = string.Empty;
    public string       RawContent  { get; init; } = string.Empty;
    public string       Answer      { get; init; } = string.Empty;
    public List<string> Suggestions { get; init; } = [];
}

public record AgentChatResponseDto
{
    public List<AgentResponseDto> Responses      { get; init; } = [];
    public List<string>           Suggestions    { get; init; } = [];
    public List<ChatMessageDto>   UpdatedHistory { get; init; } = [];
}
