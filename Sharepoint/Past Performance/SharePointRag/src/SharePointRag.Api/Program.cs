using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Scalar.AspNetCore;
using SharePointRag.Agent;
using SharePointRag.Core.Extensions;
using SharePointRag.PastPerformance;
using SharePointRag.PastPerformance.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging
    .ClearProviders()
    .AddConsole()
    .AddDebug();

// ── SharePoint RAG Core (crawler, chunker, embedder, SharpCoreDB vector store)
builder.Services.AddSharePointRag(builder.Configuration);

// ── Past Performance Agent layer ──────────────────────────────────────────────
builder.Services.AddPastPerformanceAgent();

// ── Microsoft.Agents SDK ──────────────────────────────────────────────────────
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

// General SharePoint RAG agent (Teams / WebChat)
builder.Services.AddAgent<SharePointRagAgent>(ab =>
    ab.WithOptions(o => o.StartTypingTimer = false));

// Past Performance specialist agent – registered on a separate route
builder.Services.AddAgent<PastPerformanceAgent>("pastperformance", ab =>
    ab.WithOptions(o => o.StartTypingTimer = false));

// ── API services ──────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ── Agent endpoints ───────────────────────────────────────────────────────────

// General SharePoint RAG bot (Teams default bot endpoint)
app.MapPost("/api/messages", async (
    HttpContext ctx,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken ct) =>
    await adapter.ProcessAsync(ctx.Request, ctx.Response, agent, ct));

// Past Performance specialist bot (separate Teams app / Direct Line channel)
app.MapPost("/api/pastperformance/messages", async (
    HttpContext ctx,
    IAgentHttpAdapter adapter,
    CancellationToken ct) =>
{
    // Resolve the named Past Performance agent
    var ppAgent = ctx.RequestServices
        .GetRequiredKeyedService<IAgent>("pastperformance");
    await adapter.ProcessAsync(ctx.Request, ctx.Response, ppAgent, ct);
});

// ── Infrastructure endpoints ──────────────────────────────────────────────────
app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
