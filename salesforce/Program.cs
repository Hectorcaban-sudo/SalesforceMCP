using Microsoft.Extensions.AI;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Register IChatClient using the Microsoft.Extensions.AI Anthropic provider
var apiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured.");
var model = builder.Configuration["Anthropic:Model"] ?? "claude-opus-4-5";

builder.Services.AddSingleton<IChatClient>(
    new AnthropicClientBuilder(apiKey)
        .UseAnthropicChatClient(model)
        .Build()
);

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Chat API",
        Version     = "v1",
        Description = "ASP.NET Core Web API — uses Microsoft.Extensions.AI IChatClient with Anthropic."
    });
});

// ── App ──────────────────────────────────────────────────────────────────────
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

// ── Minimal API endpoint (replaces ChatController) ───────────────────────────
app.MapPost("/api/chat", async (
    List<ChatMessage> messages,
    IChatClient chatClient,
    CancellationToken cancellationToken) =>
{
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });

    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Text)))
        return Results.BadRequest(new { error = "All messages must have non-empty text content." });

    if (messages.Last().Role != ChatRole.User)
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    var result = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);

    var replyText = string.Concat(
        result.Message.Contents
              .OfType<TextContent>()
              .Select(tc => tc.Text));

    var updatedHistory = new List<ChatMessage>(messages)
    {
        new(ChatRole.Assistant, replyText)
    };

    return Results.Ok(updatedHistory);
});

app.Run();
