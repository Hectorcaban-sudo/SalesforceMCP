using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;
using SharePointRag.Agent;
using SharePointRag.Core.Extensions;
using SharePointRag.PastPerformance;
using SharePointRag.PastPerformance.Extensions;
using SharePointRag.PastPerformance.Workflow;

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
builder.Services.AddSharePointRag(builder.Configuration);

// ── Per-agent options ─────────────────────────────────────────────────────────
builder.Services.Configure<SharePointRagAgentOptions>(
    builder.Configuration.GetSection(SharePointRagAgentOptions.SectionName));

// ── Past Performance — domain services + stateless bot ───────────────────────
// Registers: IPastPerformanceOrchestrator, IQueryParser, IContractExtractor,
//            IRelevanceScorer, IProposalDrafter, IPluginRouter
builder.Services.AddPastPerformanceAgent(builder.Configuration);

// ── Past Performance — MAF Workflow services ──────────────────────────────────
// Registers: PPOrchestratorChatClient, IChatClient (Azure OpenAI as IChatClient)
// The workflow graph itself is built below using builder.AddWorkflow().
builder.Services.AddPastPerformanceWorkflow(builder.Configuration);

// ── Microsoft.Agents SDK — Bot Framework adapter (stateless bots) ─────────────
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

// General SharePoint RAG bot (old SDK — AgentApplication)
builder.Services.AddAgent<SharePointRagAgent>(ab =>
    ab.WithOptions(o => o.StartTypingTimer = false));

// Past Performance specialist bot — stateless (old SDK — AgentApplication)
builder.Services.AddAgent<PastPerformanceAgent>("pastperformance", ab =>
    ab.WithOptions(o => o.StartTypingTimer = false));

// ── Microsoft.Agents.AI.Workflows — proper MAF workflow ──────────────────────
//
// The Past Performance Workflow is built as a 3-step sequential graph:
//   QueryParserAgent → PPOrchestratorAgent → ResponseFormatterAgent
//
// builder.AddWorkflow()   — registers the Workflow in the MAF hosting layer
// .AddAsAIAgent()         — converts the Workflow to an AIAgent keyed "pp-workflow"
//                           so PPWorkflowController can resolve it from DI
//
// Session state is managed by AgentSession (multi-turn, serialisable).
// Endpoint: POST /api/pastperformance/workflow/run  (PPWorkflowController)
builder.AddWorkflow("pp-workflow", PastPerformanceWorkflowFactory.Build)
       .AddAsAIAgent();

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck("registry", () =>
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Registry OK"));

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

// ── Bot Framework endpoints (old SDK — stateless AgentApplication bots) ───────
// General SharePoint RAG bot
app.MapPost("/api/messages", async (
    HttpContext ctx, IAgentHttpAdapter adapter, IAgent agent, CancellationToken ct) =>
    await adapter.ProcessAsync(ctx.Request, ctx.Response, agent, ct));

// Past Performance stateless bot
app.MapPost("/api/pastperformance/messages", async (
    HttpContext ctx, IAgentHttpAdapter adapter, CancellationToken ct) =>
{
    var ppAgent = ctx.RequestServices.GetRequiredKeyedService<IAgent>("pastperformance");
    await adapter.ProcessAsync(ctx.Request, ctx.Response, ppAgent, ct);
});

// ── MAF Workflow endpoint (new SDK — routed via PPWorkflowController) ─────────
// POST /api/pastperformance/workflow/run    — single-turn + session management
// POST /api/pastperformance/workflow/stream — streaming Server-Sent Events
// DELETE /api/pastperformance/workflow/session/{id} — clear session
// (Routes are declared in PPWorkflowController via [Route("api/pastperformance/workflow")])

// ── REST + infrastructure ─────────────────────────────────────────────────────
app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
