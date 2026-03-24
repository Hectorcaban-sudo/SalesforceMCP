using Microsoft.OpenApi.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Anthropic;

var builder = WebApplication.CreateBuilder(args);

// ── Semantic Kernel ───────────────────────────────────────────────────────────
var apiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? throw new InvalidOperationException("Anthropic:ApiKey is not configured.");
var model = builder.Configuration["Anthropic:Model"] ?? "claude-opus-4-5";

// Build the Kernel with the Anthropic chat completion connector
builder.Services.AddKernel()
    .AddAnthropicChatCompletion(model, apiKey);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "Chat API",
        Version     = "v1",
        Description = "ASP.NET Core Web API — uses Semantic Kernel with Anthropic."
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

// ── POST /api/chat ────────────────────────────────────────────────────────────
app.MapPost("/api/chat", async (
    List<ChatMessageDto> messages,
    IChatCompletionService chatService,
    CancellationToken cancellationToken) =>
{
    // ── Validation ────────────────────────────────────────────────────────────
    if (messages is not { Count: > 0 })
        return Results.BadRequest(new { error = "messages must not be empty." });

    if (messages.Any(m => string.IsNullOrWhiteSpace(m.Content)))
        return Results.BadRequest(new { error = "All messages must have non-empty content." });

    var lastRole = messages.Last().Role?.ToLowerInvariant();
    if (lastRole != "user")
        return Results.BadRequest(new { error = "The last message must have role 'user'." });

    // ── Build SK ChatHistory from incoming list ────────────────────────────────
    var chatHistory = new ChatHistory();

    foreach (var msg in messages)
    {
        var role = msg.Role?.ToLowerInvariant() switch
        {
            "user"      => AuthorRole.User,
            "assistant" => AuthorRole.Assistant,
            "system"    => AuthorRole.System,
            _           => AuthorRole.User
        };

        chatHistory.AddMessage(role, msg.Content!);
    }

    // ── Call Semantic Kernel ──────────────────────────────────────────────────
    var reply = await chatService.GetChatMessageContentAsync(
        chatHistory,
        cancellationToken: cancellationToken);

    var replyText = reply.Content ?? string.Empty;

    // ── Append assistant reply and return full history ────────────────────────
    var updatedHistory = new List<ChatMessageDto>(messages)
    {
        new() { Role = "assistant", Content = replyText }
    };

    return Results.Ok(updatedHistory);
});

app.Run();

// ── DTO ───────────────────────────────────────────────────────────────────────
// Simple flat DTO — decoupled from any AI SDK shape.
// Role:    "user" | "assistant" | "system"
// Content: the message text
public record ChatMessageDto
{
    public string? Role    { get; init; }
    public string? Content { get; init; }
}
