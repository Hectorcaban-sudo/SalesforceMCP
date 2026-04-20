// ============================================================
//  GovConRAG.Ingestion — MQTT Distributed Queue (Wolverine)
//  Transport: MQTT via Wolverine (wolverinefx.net)
//  No Azure Service Bus — fully portable messaging
// ============================================================

using GovConRAG.Core.Models;
using GovConRAG.Core.Storage;
using GovConRAG.Ingestion.Adapters;
using GovConRAG.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Wolverine;
using Wolverine.MQTT;

namespace GovConRAG.Ingestion.Queue;

// ── MQTT Message Definitions ──────────────────────────────────

/// <summary>Published when a new document arrives via webhook or admin API.</summary>
public record IngestDocumentCommand(
    Guid   DocumentId,
    string SourceRef,
    DocumentSource Source,
    string Domain,
    string TenantId,
    int    Priority = 5,
    Dictionary<string, string>? Meta = null);

/// <summary>Published by reconciliation job for batch-catch-up.</summary>
public record ReconcileSourceCommand(
    string RootRef,
    DocumentSource Source,
    string Domain,
    string TenantId);

/// <summary>Published after a chunk has been embedded and is ready to index.</summary>
public record IndexChunkCommand(
    Guid   DocumentId,
    Guid   ChunkId,
    string Content,
    float[] Embedding,
    int    ChunkIndex,
    string Domain);

/// <summary>Notification after full document ingestion completes.</summary>
public record DocumentIngestionCompletedEvent(
    Guid   DocumentId,
    int    ChunkCount,
    double DurationMs,
    bool   Success,
    string? Error = null);

// ── Wolverine MQTT Setup ──────────────────────────────────────

public static class WolverineMqttSetup
{
    /// <summary>
    /// Call from Program.cs:
    ///   builder.Host.UseWolverine(opts => WolverineMqttSetup.Configure(opts, config));
    /// </summary>
    public static void Configure(WolverineOptions opts, WolverineMqttConfig config)
    {
        opts.UseMqttTransport(mqtt =>
        {
            mqtt.BrokerUri(new Uri(config.BrokerUri));

            if (!string.IsNullOrEmpty(config.Username))
                mqtt.Credentials(config.Username, config.Password);

            if (config.UseTls)
                mqtt.UseTls();
        })
        // Topic routing
        .AddMqttTopic("govcon/ingest/document")
            .UseForPublishing<IngestDocumentCommand>()
        .AddMqttTopic("govcon/ingest/reconcile")
            .UseForPublishing<ReconcileSourceCommand>()
        .AddMqttTopic("govcon/ingest/chunk-index")
            .UseForPublishing<IndexChunkCommand>()
        .AddMqttTopic("govcon/events/ingestion-complete")
            .UseForPublishing<DocumentIngestionCompletedEvent>();

        // Local handling for this instance
        opts.ListenToMqttTopic("govcon/ingest/document");
        opts.ListenToMqttTopic("govcon/ingest/reconcile");
    }
}

public sealed class WolverineMqttConfig
{
    public string BrokerUri { get; set; } = "mqtt://localhost:1883";
    public string Username  { get; set; } = "";
    public string Password  { get; set; } = "";
    public bool   UseTls    { get; set; } = false;
}

// ── Wolverine Message Handlers ────────────────────────────────

/// <summary>
/// Handles IngestDocumentCommand from MQTT.
/// Wolverine auto-discovers handlers by convention (class name ends in Handler or has Handle method).
/// </summary>
public sealed class IngestDocumentHandler
{
    private readonly IngestionOrchestrator _orchestrator;
    private readonly ILogger<IngestDocumentHandler> _logger;

    public IngestDocumentHandler(IngestionOrchestrator orchestrator,
        ILogger<IngestDocumentHandler> logger)
    {
        _orchestrator = orchestrator;
        _logger       = logger;
    }

    // Wolverine calls this method
    public async Task<DocumentIngestionCompletedEvent> Handle(
        IngestDocumentCommand cmd, CancellationToken ct)
    {
        _logger.LogInformation("Handling ingest command for doc {DocId} source={Source}",
            cmd.DocumentId, cmd.Source);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var chunks = await _orchestrator.IngestAsync(
                cmd.DocumentId, cmd.SourceRef, cmd.Source,
                cmd.Domain, cmd.TenantId, cmd.Meta, ct);

            sw.Stop();
            return new DocumentIngestionCompletedEvent(
                cmd.DocumentId, chunks, sw.Elapsed.TotalMilliseconds, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Ingest failed for doc {DocId}", cmd.DocumentId);
            return new DocumentIngestionCompletedEvent(
                cmd.DocumentId, 0, sw.Elapsed.TotalMilliseconds, false, ex.Message);
        }
    }
}

public sealed class ReconcileSourceHandler
{
    private readonly IngestionOrchestrator _orchestrator;
    private readonly ILogger<ReconcileSourceHandler> _logger;

    public ReconcileSourceHandler(IngestionOrchestrator orchestrator,
        ILogger<ReconcileSourceHandler> logger)
    {
        _orchestrator = orchestrator;
        _logger       = logger;
    }

    public async Task Handle(ReconcileSourceCommand cmd, CancellationToken ct)
    {
        _logger.LogInformation("Starting reconciliation: {Source} root={Root}",
            cmd.Source, cmd.RootRef);

        await _orchestrator.ReconcileAsync(
            cmd.RootRef, cmd.Source, cmd.Domain, cmd.TenantId, ct);
    }
}

// ── Ingestion Orchestrator ────────────────────────────────────

public sealed class IngestionOrchestrator
{
    private readonly Dictionary<DocumentSource, ISourceAdapter> _adapters;
    private readonly EmbeddingPipeline    _embedder;
    private readonly IVectorStore         _store;
    private readonly ILogger<IngestionOrchestrator> _logger;
    private readonly IAuditLogger         _audit;

    public IngestionOrchestrator(
        IEnumerable<ISourceAdapter> adapters,
        EmbeddingPipeline embedder,
        IVectorStore store,
        IAuditLogger audit,
        ILogger<IngestionOrchestrator> logger)
    {
        _adapters = adapters.ToDictionary(a => a.SourceType);
        _embedder = embedder;
        _store    = store;
        _audit    = audit;
        _logger   = logger;
    }

    /// <summary>Full ingest pipeline: Fetch → Chunk → Embed → Index → Graph</summary>
    public async Task<int> IngestAsync(
        Guid documentId, string sourceRef, DocumentSource source,
        string domain, string tenantId,
        Dictionary<string, string>? meta,
        CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(source, out var adapter))
            throw new InvalidOperationException($"No adapter for source {source}");

        // 1. Fetch raw document
        var raw = await adapter.FetchAsync(sourceRef, meta, ct);
        if (raw == null) throw new Exception($"Adapter returned null for {sourceRef}");

        // 2. Dedup by content hash
        if (await _store.DocumentExistsByHashAsync(raw.ContentHash, ct))
        {
            _logger.LogInformation("Skipping duplicate doc (hash={Hash})", raw.ContentHash);
            return 0;
        }

        // 3. Build domain model
        var doc = new SourceDocument
        {
            Id          = documentId,
            Title       = raw.Title,
            SourceUrl   = sourceRef,
            Source      = source,
            Status      = DocumentStatus.Processing,
            MimeType    = raw.MimeType,
            ContentHash = raw.ContentHash,
            SizeBytes   = raw.SizeBytes,
            Domain      = domain,
            TenantId    = tenantId,
            Metadata    = raw.Metadata
        };

        var docNodeId = await _store.UpsertDocumentNodeAsync(doc, ct);
        doc.LiteGraphNodeId = docNodeId;

        await _audit.LogAsync(new AuditEvent
        {
            EventType    = "Ingestion.Started",
            Actor        = "IngestionOrchestrator",
            TenantId     = tenantId,
            ResourceId   = documentId.ToString(),
            ResourceType = "Document",
            Details      = new() { ["source"] = source, ["ref"] = sourceRef }
        });

        try
        {
            // 4. Chunk
            var strategy = ChunkerFactory.AutoSelect(doc);
            var chunks   = strategy.Chunk(doc, raw.Content).ToList();
            _logger.LogInformation("Chunked {DocId} into {Count} chunks using {Strategy}",
                documentId, chunks.Count, strategy.Name);

            // 5. Embed
            chunks = await _embedder.EmbedChunksAsync(chunks, ct);

            // 6. Index into LiteGraph
            foreach (var chunk in chunks)
            {
                var chunkNodeId = await _store.UpsertChunkNodeAsync(chunk, ct);
                chunk.LiteGraphNodeId = chunkNodeId;
                await _store.LinkChunkToDocumentAsync(chunkNodeId, docNodeId, ct);
            }

            // 7. Update status
            doc.Status    = DocumentStatus.Indexed;
            doc.IndexedAt = DateTime.UtcNow;
            await _store.UpdateDocumentStatusAsync(documentId, DocumentStatus.Indexed, ct: ct);

            await _audit.LogAsync(new AuditEvent
            {
                EventType    = "Ingestion.Completed",
                Actor        = "IngestionOrchestrator",
                TenantId     = tenantId,
                ResourceId   = documentId.ToString(),
                ResourceType = "Document",
                Outcome      = "Success",
                Details      = new() { ["chunkCount"] = chunks.Count }
            });

            return chunks.Count;
        }
        catch (Exception ex)
        {
            await _store.UpdateDocumentStatusAsync(
                documentId, DocumentStatus.Failed, ex.Message, ct);

            await _audit.LogAsync(new AuditEvent
            {
                EventType    = "Ingestion.Failed",
                Actor        = "IngestionOrchestrator",
                TenantId     = tenantId,
                ResourceId   = documentId.ToString(),
                ResourceType = "Document",
                Outcome      = "Failure",
                Details      = new() { ["error"] = ex.Message }
            });

            throw;
        }
    }

    /// <summary>Reconcile: enumerate all docs from source, ingest missing/changed.</summary>
    public async Task ReconcileAsync(
        string rootRef, DocumentSource source, string domain, string tenantId,
        CancellationToken ct = default)
    {
        if (!_adapters.TryGetValue(source, out var adapter)) return;

        _logger.LogInformation("Reconciling {Source} from {Root}", source, rootRef);
        int processed = 0, skipped = 0;

        await foreach (var raw in adapter.EnumerateAsync(rootRef, ct))
        {
            if (await _store.DocumentExistsByHashAsync(raw.ContentHash, ct))
            {
                skipped++;
                continue;
            }

            var docId = Guid.NewGuid();
            try
            {
                await IngestAsync(docId, raw.SourceRef, source, domain, tenantId, null, ct);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconcile skipped {Ref}", raw.SourceRef);
            }
        }

        _logger.LogInformation("Reconciliation done: processed={P} skipped={S}", processed, skipped);
    }
}

// ── Background Reconciliation Job ────────────────────────────

public sealed class ReconciliationBackgroundService : BackgroundService
{
    private readonly IMessageBus  _bus;
    private readonly ILogger<ReconciliationBackgroundService> _logger;
    private readonly ReconciliationConfig _config;

    public ReconciliationBackgroundService(
        IMessageBus bus,
        ReconciliationConfig config,
        ILogger<ReconciliationBackgroundService> logger)
    {
        _bus    = bus;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Reconciliation service started, interval={Interval}min",
            _config.IntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(_config.IntervalMinutes), stoppingToken);

            foreach (var source in _config.Sources)
            {
                _logger.LogInformation("Dispatching reconcile for {Source}", source.Source);
                await _bus.PublishAsync(new ReconcileSourceCommand(
                    source.RootRef, source.Source, source.Domain, source.TenantId));
            }
        }
    }
}

public sealed class ReconciliationConfig
{
    public int IntervalMinutes { get; set; } = 60;
    public List<ReconciliationSource> Sources { get; set; } = new();
}

public sealed class ReconciliationSource
{
    public string         RootRef  { get; set; } = "";
    public DocumentSource Source   { get; set; }
    public string         Domain   { get; set; } = "";
    public string         TenantId { get; set; } = "default";
}
