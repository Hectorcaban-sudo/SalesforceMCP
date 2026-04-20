// ============================================================
//  GovConRAG.Api — ASP.NET Core Minimal API
//  Admin | Query | Webhook | Metrics | Audit
// ============================================================

using GovConRAG.Agents;
using GovConRAG.Core.Models;
using GovConRAG.Core.Storage;
using GovConRAG.Infrastructure;
using GovConRAG.Ingestion.Adapters;
using GovConRAG.Ingestion.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// ── Config ─────────────────────────────────────────────────────
var config = builder.Configuration.GetSection("GovConRag").Get<GovConRagConfig>()
             ?? new GovConRagConfig();

// ── Services ───────────────────────────────────────────────────
builder.Services.AddGovConRag(config);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "GovConRAG API", Version = "v1" }));
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
builder.Services.AddResponseCaching();

// ── Wolverine MQTT ─────────────────────────────────────────────
builder.Host.UseWolverine(opts =>
{
    WolverineMqttSetup.Configure(opts, config.Mqtt);
    opts.Discovery.IncludeAssembly(typeof(IngestDocumentHandler).Assembly);
});

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseResponseCaching();

// ── Health ────────────────────────────────────────────────────
app.MapGet("/health", () => new { status = "ok", utc = DateTime.UtcNow })
   .WithTags("System");

// ═══════════════════════════════════════════════════════════════
//  QUERY API
// ═══════════════════════════════════════════════════════════════

var queryGroup = app.MapGroup("/api/query").WithTags("Query");

queryGroup.MapPost("/", async (
    [FromBody] RagQuery query,
    AgentOrchestrator orchestrator,
    IAuditLogger audit,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(query.Question))
        return Results.BadRequest("Question is required");

    query.UserId = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "anonymous";

    var result = await orchestrator.HandleQueryAsync(query, ct);
    return Results.Ok(result);
})
.WithSummary("Submit a RAG query — automatically routed to the correct specialist agent");

queryGroup.MapPost("/performance-review", async (
    [FromQuery] string contractNumber,
    PastPerformanceAgent agent, CancellationToken ct) =>
{
    var review = await agent.ReviewAsync(contractNumber, ct);
    return Results.Ok(review);
})
.WithSummary("Generate a past performance review for a contract number");

queryGroup.MapPost("/rfp-analyze", async (
    [FromBody] RfpAnalysisRequest req,
    ProposalAgent agent, CancellationToken ct) =>
{
    var result = await agent.AnalyzeRfpAsync(req.RfpText, ct);
    return Results.Ok(result);
})
.WithSummary("Analyze an RFP and calculate match/win probability");

queryGroup.MapPost("/rfp/{id}/generate-volume", async (
    Guid id,
    [FromQuery] string volume,
    [FromBody] RfpOpportunity opportunity,
    ProposalAgent agent, CancellationToken ct) =>
{
    opportunity.Id = id;
    var draft = await agent.GenerateProposalVolumeAsync(opportunity, volume, ct);
    return Results.Ok(draft);
})
.WithSummary("Generate proposal volume (technical/management/sow) for an RFP");

queryGroup.MapPost("/competitor-analysis", async (
    [FromBody] RfpOpportunity opportunity,
    CompetitorAgent agent, CancellationToken ct) =>
{
    var analysis = await agent.AnalyzeAsync(opportunity, ct);
    return Results.Ok(analysis);
})
.WithSummary("Run competitor analysis and win probability for an opportunity");

// ═══════════════════════════════════════════════════════════════
//  INGESTION ADMIN API
// ═══════════════════════════════════════════════════════════════

var ingestGroup = app.MapGroup("/api/admin/ingest").WithTags("Ingestion");

ingestGroup.MapPost("/document", async (
    [FromBody] IngestDocumentRequest req,
    IMessageBus bus,
    IAuditLogger audit,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var docId = Guid.NewGuid();
    await bus.PublishAsync(new IngestDocumentCommand(
        docId, req.SourceRef, req.Source, req.Domain, req.TenantId ?? "default",
        req.Priority, req.Metadata));

    await audit.LogAsync(new AuditEvent
    {
        EventType    = "Ingest.Enqueued",
        Actor        = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "admin",
        TenantId     = req.TenantId ?? "default",
        ResourceId   = docId.ToString(),
        ResourceType = "Document",
        Details      = new() { ["source"] = req.Source, ["ref"] = req.SourceRef }
    });

    return Results.Accepted($"/api/admin/ingest/status/{docId}", new { documentId = docId });
})
.WithSummary("Enqueue a document for ingestion via MQTT");

ingestGroup.MapPost("/reconcile", async (
    [FromBody] ReconcileRequest req,
    IMessageBus bus, CancellationToken ct) =>
{
    await bus.PublishAsync(new ReconcileSourceCommand(
        req.RootRef, req.Source, req.Domain, req.TenantId ?? "default"));
    return Results.Accepted(null, new { message = "Reconciliation job dispatched" });
})
.WithSummary("Trigger a reconciliation scan for a source");

ingestGroup.MapPost("/sharepoint/bulk", async (
    [FromBody] SharePointBulkRequest req,
    SharePointAdapter adapter,
    IMessageBus bus, CancellationToken ct) =>
{
    int queued = 0;
    await foreach (var raw in adapter.EnumerateAsync($"{req.SiteId}|{req.DriveId}|root", ct))
    {
        var docId = Guid.NewGuid();
        await bus.PublishAsync(new IngestDocumentCommand(
            docId, raw.SourceRef, DocumentSource.SharePoint,
            req.Domain, req.TenantId, 5, raw.Metadata));
        queued++;

        if (queued >= 100_000) break; // Safety cap
    }
    return Results.Accepted(null, new { queued, message = $"Queued {queued} SharePoint documents" });
})
.WithSummary("Bulk-ingest up to 100K SharePoint documents");

ingestGroup.MapGet("/status/{docId}", async (
    Guid docId, IVectorStore store, CancellationToken ct) =>
{
    var docs = await store.GetDocumentsByStatusAsync(DocumentStatus.Processing, 1000, ct);
    var doc  = docs.FirstOrDefault(d => d.Id == docId);
    return doc != null
        ? Results.Ok(new { docId, status = doc.Status, error = doc.ErrorMessage })
        : Results.NotFound(new { docId, status = "unknown" });
})
.WithSummary("Check ingestion status of a document");

// ═══════════════════════════════════════════════════════════════
//  SHAREPOINT WEBHOOK ENDPOINT
// ═══════════════════════════════════════════════════════════════

var webhookGroup = app.MapGroup("/api/webhooks").WithTags("Webhooks");

// SharePoint sends a validationToken query param on subscription setup
webhookGroup.MapPost("/sharepoint", async (
    [FromQuery] string? validationToken,
    [FromBody] SharePointNotificationPayload? payload,
    SharePointAdapter spAdapter,
    IMessageBus bus,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    // 1. Validation handshake
    if (!string.IsNullOrEmpty(validationToken))
        return Results.Content(validationToken, "text/plain");

    if (payload?.Value == null) return Results.BadRequest();

    // 2. Process each notification
    foreach (var notification in payload.Value)
    {
        logger.LogInformation("SharePoint webhook notification: resource={R}", notification.Resource);

        var changedIds = await spAdapter.ProcessWebhookNotificationAsync(
            notification.SiteId, notification.DriveId,
            notification.SubscriptionId, notification.DeltaToken ?? "", ct);

        foreach (var itemRef in changedIds)
        {
            await bus.PublishAsync(new IngestDocumentCommand(
                Guid.NewGuid(), itemRef, DocumentSource.SharePoint,
                notification.Domain ?? "contracts", notification.TenantId ?? "default", 9));
        }
    }

    return Results.NoContent();
})
.WithSummary("Real-time SharePoint change notifications webhook");

// ═══════════════════════════════════════════════════════════════
//  METRICS & MONITORING
// ═══════════════════════════════════════════════════════════════

var metricsGroup = app.MapGroup("/api/metrics").WithTags("Metrics");

metricsGroup.MapGet("/ingestion", async (IVectorStore store, CancellationToken ct) =>
    Results.Ok(await store.GetIngestionMetricsAsync(ct)))
    .WithSummary("Ingestion pipeline metrics");

metricsGroup.MapGet("/performance-report", async (
    IVectorStore store,
    PerformanceMonitorAgent perfAgent,
    CancellationToken ct) =>
{
    var ingestion = await store.GetIngestionMetricsAsync(ct);
    var queries   = new QueryMetrics { TotalQueries = 0, AvgLatencyMs = 0 }; // wire real metrics
    var report    = await perfAgent.GenerateReportAsync(ingestion, queries, ct);
    return Results.Ok(report);
})
.WithSummary("AI-generated system performance report");

metricsGroup.MapGet("/system", () => Results.Ok(new
{
    memory     = GC.GetTotalMemory(false),
    threads    = System.Threading.ThreadPool.ThreadCount,
    uptime     = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToString(),
    dotnetVer  = Environment.Version.ToString()
}))
.WithSummary("Basic system metrics");

// ═══════════════════════════════════════════════════════════════
//  AUDIT API
// ═══════════════════════════════════════════════════════════════

var auditGroup = app.MapGroup("/api/audit").WithTags("Audit");

auditGroup.MapGet("/events", async (
    [FromQuery] string? tenantId,
    [FromQuery] string? eventType,
    [FromQuery] DateTime? from,
    [FromQuery] DateTime? to,
    [FromQuery] int limit,
    IAuditLogger audit, CancellationToken ct) =>
{
    var events = await audit.QueryAsync(tenantId, eventType, from, to, limit > 0 ? limit : 200, ct);
    return Results.Ok(events);
})
.WithSummary("Query FedRAMP-style audit log (AU-2/AU-3/AU-12 compliant)");

app.Run();

// ═══════════════════════════════════════════════════════════════
//  Request / Response DTOs
// ═══════════════════════════════════════════════════════════════

public sealed record IngestDocumentRequest(
    string SourceRef,
    DocumentSource Source,
    string Domain,
    string? TenantId,
    int Priority = 5,
    Dictionary<string, string>? Metadata = null);

public sealed record ReconcileRequest(
    string RootRef,
    DocumentSource Source,
    string Domain,
    string? TenantId);

public sealed record SharePointBulkRequest(
    string SiteId,
    string DriveId,
    string Domain,
    string TenantId = "default");

public sealed record RfpAnalysisRequest(string RfpText);

public sealed class SharePointNotificationPayload
{
    public List<SharePointNotification>? Value { get; set; }
}

public sealed class SharePointNotification
{
    public string  SubscriptionId  { get; set; } = "";
    public string  Resource        { get; set; } = "";
    public string  SiteId          { get; set; } = "";
    public string  DriveId         { get; set; } = "";
    public string? DeltaToken      { get; set; }
    public string? Domain          { get; set; }
    public string? TenantId        { get; set; }
}
