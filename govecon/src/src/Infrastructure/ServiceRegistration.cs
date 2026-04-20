// ============================================================
//  GovConRAG.Infrastructure — Audit + DI Registration
//  FedRAMP AU-2 compliant audit trail
// ============================================================

using GovConRAG.Core.Models;
using GovConRAG.Core.Storage;
using GovConRAG.Ingestion;
using GovConRAG.Ingestion.Adapters;
using GovConRAG.Ingestion.Queue;
using GovConRAG.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.OpenAI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using Serilog;
using Serilog.Events;
using System.Text.Json;

namespace GovConRAG.Infrastructure;

// ── Audit Logger (FedRAMP AU-2 / AU-3 / AU-12) ───────────────

public interface IAuditLogger
{
    Task LogAsync(AuditEvent evt, CancellationToken ct = default);
    Task<List<AuditEvent>> QueryAsync(string? tenantId, string? eventType,
        DateTime? from, DateTime? to, int limit = 200, CancellationToken ct = default);
}

public sealed class SerilogAuditLogger : IAuditLogger
{
    private readonly Serilog.ILogger _auditLog;
    private readonly List<AuditEvent> _buffer = new();  // In production: use persistent DB
    private readonly object _lock = new();

    public SerilogAuditLogger()
    {
        _auditLog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: "logs/audit-.jsonl",
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Message:lj}{NewLine}",
                restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[AUDIT] {Message:lj}{NewLine}")
            .CreateLogger();
    }

    public Task LogAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            WriteIndented  = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Structured log — immutable, append-only (FedRAMP AU-9)
        _auditLog.Information("{AuditEvent}", json);

        lock (_lock)
        {
            _buffer.Add(evt);
            // Keep last 10k in memory for dashboard queries
            if (_buffer.Count > 10_000) _buffer.RemoveAt(0);
        }

        return Task.CompletedTask;
    }

    public Task<List<AuditEvent>> QueryAsync(
        string? tenantId, string? eventType,
        DateTime? from, DateTime? to, int limit = 200, CancellationToken ct = default)
    {
        IEnumerable<AuditEvent> q = _buffer;

        if (tenantId != null)  q = q.Where(e => e.TenantId == tenantId);
        if (eventType != null) q = q.Where(e => e.EventType == eventType);
        if (from.HasValue)     q = q.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue)       q = q.Where(e => e.OccurredAt <= to.Value);

        return Task.FromResult(q.OrderByDescending(e => e.OccurredAt).Take(limit).ToList());
    }
}

// ── Configuration POCOs ───────────────────────────────────────

public sealed class GovConRagConfig
{
    public string OpenAiApiKey          { get; set; } = "";
    public string ChatModel             { get; set; } = "gpt-4o";
    public string EmbeddingModel        { get; set; } = "text-embedding-3-small";
    public string LiteGraphDbPath       { get; set; } = "data/govcon.db";
    public string TenantName            { get; set; } = "GovConRAG";
    public string GraphName             { get; set; } = "KnowledgeGraph";
    public SharePointConfig SharePoint  { get; set; } = new();
    public WolverineMqttConfig Mqtt     { get; set; } = new();
    public ReconciliationConfig Reconciliation { get; set; } = new();
}

public sealed class SharePointConfig
{
    public string TenantId     { get; set; } = "";
    public string ClientId     { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

// ── DI Registration ───────────────────────────────────────────

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGovConRag(
        this IServiceCollection services,
        GovConRagConfig config)
    {
        // ── AI Clients ─────────────────────────────────────────
        var openAiClient = new OpenAIClient(config.OpenAiApiKey);

        services.AddSingleton<IChatClient>(_ =>
            openAiClient.GetChatClient(config.ChatModel)
                        .AsIChatClient()
                        .UseFunctionInvocation()
                        .UseLogging());

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
            openAiClient.GetEmbeddingClient(config.EmbeddingModel)
                        .AsIEmbeddingGenerator());

        // ── LiteGraph Vector Store ─────────────────────────────
        services.AddSingleton<IVectorStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LiteGraphVectorStore>>();
            // Sync create — wrap in GetAwaiter for DI
            return LiteGraphVectorStore.CreateAsync(
                config.LiteGraphDbPath,
                config.TenantName,
                config.GraphName,
                logger).GetAwaiter().GetResult();
        });

        // ── Audit ──────────────────────────────────────────────
        services.AddSingleton<IAuditLogger, SerilogAuditLogger>();

        // ── Embedding Pipeline ─────────────────────────────────
        services.AddSingleton<EmbeddingPipeline>();

        // ── Source Adapters ────────────────────────────────────
        services.AddHttpClient();
        services.AddSingleton<ISourceAdapter>(sp =>
            new SharePointAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                config.SharePoint.TenantId,
                config.SharePoint.ClientId,
                config.SharePoint.ClientSecret,
                sp.GetRequiredService<ILogger<SharePointAdapter>>()));

        services.AddSingleton<ISourceAdapter>(sp =>
            new ExcelAdapter(sp.GetRequiredService<ILogger<ExcelAdapter>>()));

        services.AddSingleton<ISourceAdapter>(sp =>
            new CustomApiAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                "https://api.example.com", null,
                sp.GetRequiredService<ILogger<CustomApiAdapter>>()));

        // ── Ingestion Orchestrator ─────────────────────────────
        services.AddSingleton<IngestionOrchestrator>();

        // ── Agents ─────────────────────────────────────────────
        services.AddSingleton<RouterAgent>(sp =>
            new RouterAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<ILogger<RouterAgent>>()));

        services.AddSingleton<AccountsAgent>(sp =>
            new AccountsAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IAuditLogger>()));

        services.AddSingleton<ContractsAgent>(sp =>
            new ContractsAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

        services.AddSingleton<OperationsAgent>(sp =>
            new OperationsAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

        services.AddSingleton<PastPerformanceAgent>(sp =>
            new PastPerformanceAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

        services.AddSingleton<ProposalAgent>(sp =>
            new ProposalAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient()));

        services.AddSingleton<CompetitorAgent>(sp =>
            new CompetitorAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient()));

        services.AddSingleton<PerformanceMonitorAgent>(sp =>
            new PerformanceMonitorAgent(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));

        services.AddSingleton<AgentOrchestrator>();

        // ── Background Services ────────────────────────────────
        services.AddSingleton(config.Reconciliation);
        services.AddHostedService<ReconciliationBackgroundService>();

        return services;
    }
}
