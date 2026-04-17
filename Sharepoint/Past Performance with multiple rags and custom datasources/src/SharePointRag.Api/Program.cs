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
    .AddJsonFile("appsettings.json",                                    optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging
    .ClearProviders()
    .AddConsole()
    .AddDebug();

// ── Core RAG infrastructure ───────────────────────────────────────────────────
// Reads RagRegistry from configuration and builds:
//   • One IDataSourceConnector per data source
//   • One IVectorStore        per system  (isolated SharpCoreDB sub-directory)
//   • One IIndexStateStore    per system
//   • One IIndexingPipeline   per system  (fans across its assigned libraries)
//   • ILibraryRegistry        (central lookup)
//   • IRagOrchestratorFactory (creates multi-system orchestrators on demand)
builder.Services.AddSharePointRag(builder.Configuration);

// ── Per-agent options ─────────────────────────────────────────────────────────
// Each agent declares which RAG systems it covers via its own options section.
// All names must match RagRegistry.Systems[*].Name in appsettings.
builder.Services.Configure<SharePointRagAgentOptions>(
    builder.Configuration.GetSection(SharePointRagAgentOptions.SectionName));

// ── Past Performance Agent layer ──────────────────────────────────────────────
builder.Services.AddPastPerformanceAgent(builder.Configuration);

// ── Microsoft.Agents SDK ──────────────────────────────────────────────────────
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

// General SharePoint RAG bot
// Searches the systems listed in SharePointRagAgent.SystemNames (default: ["General"])
builder.Services.AddAgent<SharePointRagAgent>(ab =>
    ab.WithOptions(o => o.StartTypingTimer = false));

// Past Performance specialist bot
// Searches the systems listed in PastPerformanceAgent.SystemNames
builder.Services.AddAgent<PastPerformanceAgent>("pastperformance", ab =>
    ab.WithOptions(o => o.StartTypingTimer = false));

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck("registry", () =>
    {
        // Light health check — registry was built successfully if we get here
        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Registry OK");
    });

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

// ── Bot endpoints ─────────────────────────────────────────────────────────────
// General SharePoint RAG bot
app.MapPost("/api/messages", async (
    HttpContext ctx, IAgentHttpAdapter adapter, IAgent agent, CancellationToken ct) =>
    await adapter.ProcessAsync(ctx.Request, ctx.Response, agent, ct));

// Past Performance specialist bot (separate Teams app registration)
app.MapPost("/api/pastperformance/messages", async (
    HttpContext ctx, IAgentHttpAdapter adapter, CancellationToken ct) =>
{
    var ppAgent = ctx.RequestServices.GetRequiredKeyedService<IAgent>("pastperformance");
    await adapter.ProcessAsync(ctx.Request, ctx.Response, ppAgent, ct);
});

// ── REST + infrastructure ─────────────────────────────────────────────────────
app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
