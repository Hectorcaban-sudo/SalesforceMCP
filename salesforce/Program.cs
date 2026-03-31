using System.Text.Json;
using ChatApp.Agents;
using ChatApp.Models;
using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Anthropic;

var builder = WebApplication.CreateBuilder(args);

var apiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured.");
var model = builder.Configuration["Anthropic:Model"] ?? "claude-opus-4-5";

builder.Services
    .AddKernel()
    .AddAnthropicChatCompletion(model, apiKey);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Salesforce SOQL Agent API",
        Version = "v1",
        Description = "Clean API + Semantic Kernel GroupChatOrchestration for Accounts, Opportunities, and Contracts."
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

var salesforceApi = app.MapGroup("/api/salesforce").WithTags("Salesforce Agents");

salesforceApi.MapPost("/agent-chat", async (
    List<ChatMessageDto> messages,
    Kernel kernel,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
    {
        return Results.BadRequest(new { error = "messages must contain at least one item." });
    }

    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Role) || string.IsNullOrWhiteSpace(m.Content)))
    {
        return Results.BadRequest(new { error = "Each message requires role and content." });
    }

    var responseBuffer = new List<ChatMessageContent>();

    var manager = new SalesforceGroupChatManager(kernel);
    var orchestration = new GroupChatOrchestration(
        manager,
        SalesforceSoqlAgents.CreateAccountsAgent(kernel),
        SalesforceSoqlAgents.CreateOpportunitiesAgent(kernel),
        SalesforceSoqlAgents.CreateContractsAgent(kernel))
    {
        ResponseCallback = msg =>
        {
            responseBuffer.Add(msg);
            return ValueTask.CompletedTask;
        }
    };

    var runtime = new InProcessRuntime();
    await runtime.StartAsync(cancellationToken);

    try
    {
        var task = BuildTaskWithHistory(messages);
        var result = await orchestration.InvokeAsync(task, runtime, cancellationToken);
        var finalText = await result.GetValueAsync(TimeSpan.FromSeconds(90), cancellationToken);

        var parsedResponses = ParseAgentResponses(responseBuffer, finalText);

        var historyEntryText = string.Join("\n\n", parsedResponses.Select(r =>
            $"[{r.AgentName}] SOQL: {r.Soql}\nReason: {r.Explanation}"));

        var response = new AgentChatResponseDto
        {
            AgentResponses = parsedResponses,
            UpdatedHistory =
            [
                .. messages,
                new ChatMessageDto { Role = "assistant", Content = historyEntryText }
            ]
        };

        return Results.Ok(response);
    }
    finally
    {
        await runtime.RunUntilIdleAsync();
    }
})
.WithName("SalesforceAgentChat")
.WithSummary("Routes messages to Accounts, Opportunities, or Contracts agent and returns SOQL.")
.WithDescription("Accepts message history (role/content), uses GroupChatOrchestration, and returns generated SOQL plus updated history.");

app.Run();

static string BuildTaskWithHistory(IEnumerable<ChatMessageDto> messages)
{
    var history = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));
    return $$"""
        Convert the latest user request to valid SOQL.
        Use prior chat history for context.

        Conversation history:
        {{history}}
        """;
}

static List<AgentResponseDto> ParseAgentResponses(
    IEnumerable<ChatMessageContent> responseBuffer,
    string fallbackText)
{
    var parsed = new List<AgentResponseDto>();

    foreach (var response in responseBuffer.Where(r => r.Role == AuthorRole.Assistant))
    {
        var raw = response.Content ?? string.Empty;
        var data = TryParseAgentResponse(raw);
        if (data is not null)
        {
            parsed.Add(data);
        }
    }

    if (parsed.Count == 0)
    {
        parsed.Add(TryParseAgentResponse(fallbackText) ?? new AgentResponseDto
        {
            AgentName = "SalesforceAgent",
            Soql = "SELECT Id FROM Account LIMIT 1",
            Explanation = fallbackText
        });
    }

    return parsed;
}

static AgentResponseDto? TryParseAgentResponse(string raw)
{
    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var agent = root.TryGetProperty("agentName", out var a) ? a.GetString() : "SalesforceAgent";
        var soql = root.TryGetProperty("soql", out var s) ? s.GetString() : string.Empty;
        var explanation = root.TryGetProperty("explanation", out var e) ? e.GetString() : string.Empty;

        if (string.IsNullOrWhiteSpace(soql))
        {
            return null;
        }

        return new AgentResponseDto
        {
            AgentName = agent ?? "SalesforceAgent",
            Soql = soql,
            Explanation = explanation ?? string.Empty
        };
    }
    catch
    {
        return null;
    }
}
