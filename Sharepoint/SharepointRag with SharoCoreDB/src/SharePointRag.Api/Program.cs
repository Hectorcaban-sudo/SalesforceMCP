using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Scalar.AspNetCore;
using SharePointRag.Agent;
using SharePointRag.Core.Extensions;

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

// ── SharePoint RAG Core services ──────────────────────────────────────────────
builder.Services.AddSharePointRag(builder.Configuration);

// ── Microsoft.Agents SDK ──────────────────────────────────────────────────────
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

builder.Services.AddAgent<SharePointRagAgent>(agentBuilder =>
{
    // AgentApplicationOptions – turn-state type defaults to TurnState
    agentBuilder.WithOptions(opts =>
    {
        opts.StartTypingTimer = false; // we send typing manually
    });
});

// ── API services ──────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Microsoft.Agents message endpoint
app.MapPost("/api/messages", async (HttpContext httpContext,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken ct) =>
{
    await adapter.ProcessAsync(httpContext.Request, httpContext.Response, agent, ct);
});

// Health check
app.MapHealthChecks("/health");

// REST endpoints for RAG and indexing
app.MapControllers();

app.Run();
