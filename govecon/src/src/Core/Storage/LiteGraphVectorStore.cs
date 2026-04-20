// ============================================================
//  GovConRAG.Core — LiteGraphVectorStore
//  Extends LiteGraph with typed RAG operations.
//  Implements IVectorStore for pluggable usage.
// ============================================================

using GovConRAG.Core.Models;
using LiteGraph;
using LiteGraph.GraphRepositories.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Specialized;
using System.Text.Json;

namespace GovConRAG.Core.Storage;

public interface IVectorStore
{
    Task<Guid> UpsertDocumentNodeAsync(SourceDocument doc, CancellationToken ct = default);
    Task<Guid> UpsertChunkNodeAsync(DocumentChunk chunk, CancellationToken ct = default);
    Task LinkChunkToDocumentAsync(Guid chunkNodeId, Guid docNodeId, CancellationToken ct = default);
    Task<List<RetrievedChunk>> VectorSearchAsync(float[] queryEmbedding, string? domain, int topK, float minScore, CancellationToken ct = default);
    Task<List<GraphContext>> GraphContextAsync(Guid documentNodeId, int depth = 2, CancellationToken ct = default);
    Task<List<SourceDocument>> GetDocumentsByStatusAsync(DocumentStatus status, int limit = 100, CancellationToken ct = default);
    Task UpdateDocumentStatusAsync(Guid docId, DocumentStatus status, string? error = null, CancellationToken ct = default);
    Task<IngestionMetrics> GetIngestionMetricsAsync(CancellationToken ct = default);
    Task<bool> DocumentExistsByHashAsync(string contentHash, CancellationToken ct = default);
}

public sealed class LiteGraphVectorStore : IVectorStore, IAsyncDisposable
{
    // ── labels used as node types ──────────────────────────────
    private const string LabelDoc   = "Document";
    private const string LabelChunk = "Chunk";
    private const string LabelEdge  = "ContainsChunk";

    private readonly LiteGraphClient _client;
    private readonly TenantMetadata  _tenant;
    private readonly Graph           _graph;
    private readonly ILogger<LiteGraphVectorStore> _logger;

    private LiteGraphVectorStore(
        LiteGraphClient client,
        TenantMetadata tenant,
        Graph graph,
        ILogger<LiteGraphVectorStore> logger)
    {
        _client = client;
        _tenant = tenant;
        _graph  = graph;
        _logger = logger;
    }

    // ── Factory (async init) ──────────────────────────────────

    public static async Task<LiteGraphVectorStore> CreateAsync(
        string dbPath,
        string tenantName,
        string graphName,
        ILogger<LiteGraphVectorStore> logger,
        CancellationToken ct = default)
    {
        var client = new LiteGraphClient(new SqliteGraphRepository(dbPath));
        client.InitializeRepository();

        // Ensure tenant
        var tenants = new List<TenantMetadata>();
        await foreach (var t in client.Tenant.EnumerateAsync(ct: ct))
            tenants.Add(t);

        TenantMetadata tenant = tenants.FirstOrDefault(t => t.Name == tenantName)
            ?? await client.Tenant.CreateAsync(new TenantMetadata { Name = tenantName }, ct);

        // Ensure graph
        var graphs = new List<Graph>();
        await foreach (var g in client.Graph.EnumerateAsync(tenant.GUID, ct: ct))
            graphs.Add(g);

        Graph graph = graphs.FirstOrDefault(g => g.Name == graphName)
            ?? await client.Graph.CreateAsync(new Graph
            {
                TenantGUID = tenant.GUID,
                Name       = graphName
            }, ct);

        logger.LogInformation("LiteGraphVectorStore ready: tenant={Tenant} graph={Graph} db={Db}",
            tenantName, graphName, dbPath);

        return new LiteGraphVectorStore(client, tenant, graph, logger);
    }

    // ── Upsert / Index ─────────────────────────────────────────

    public async Task<Guid> UpsertDocumentNodeAsync(SourceDocument doc, CancellationToken ct = default)
    {
        var data = new
        {
            doc.Id, doc.Title, doc.SourceUrl, doc.Source,
            doc.Status, doc.MimeType, doc.ContentHash,
            doc.Domain, doc.TenantId, doc.Metadata,
            doc.CreatedAt, doc.IndexedAt
        };

        Node? existing = doc.LiteGraphNodeId.HasValue
            ? await TryGetNodeAsync(doc.LiteGraphNodeId.Value, ct)
            : null;

        if (existing != null)
        {
            existing.Data = data;
            existing.Labels = new List<string> { LabelDoc, doc.Domain };
            await _client.Node.UpdateAsync(existing, ct);
            _logger.LogDebug("Updated document node {NodeId}", existing.GUID);
            return existing.GUID;
        }

        var node = await _client.Node.CreateAsync(new Node
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            Name       = doc.Title,
            Labels     = new List<string> { LabelDoc, doc.Domain },
            Data       = data,
            Tags       = BuildTags(new Dictionary<string, string>
            {
                ["source"] = doc.Source.ToString(),
                ["domain"] = doc.Domain,
                ["status"] = doc.Status.ToString()
            })
        }, ct);

        _logger.LogDebug("Created document node {NodeId} for doc {DocId}", node.GUID, doc.Id);
        return node.GUID;
    }

    public async Task<Guid> UpsertChunkNodeAsync(DocumentChunk chunk, CancellationToken ct = default)
    {
        var vectors = chunk.Embedding != null
            ? new List<VectorMetadata>
            {
                new()
                {
                    Model         = "text-embedding-3-small",
                    Dimensionality = chunk.Embedding.Length,
                    Content       = chunk.Content[..Math.Min(500, chunk.Content.Length)],
                    Vectors       = chunk.Embedding.ToList()
                }
            }
            : null;

        Node? existing = chunk.LiteGraphNodeId.HasValue
            ? await TryGetNodeAsync(chunk.LiteGraphNodeId.Value, ct)
            : null;

        if (existing != null)
        {
            existing.Data    = new { chunk.Id, chunk.DocumentId, chunk.ChunkIndex, chunk.Content, chunk.TokenCount };
            existing.Vectors = vectors;
            await _client.Node.UpdateAsync(existing, ct);
            return existing.GUID;
        }

        var node = await _client.Node.CreateAsync(new Node
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            Name       = $"Chunk-{chunk.ChunkIndex}",
            Labels     = new List<string> { LabelChunk },
            Data       = new { chunk.Id, chunk.DocumentId, chunk.ChunkIndex, chunk.Content, chunk.TokenCount, chunk.Metadata },
            Tags       = BuildTags(new Dictionary<string, string>
            {
                ["docId"] = chunk.DocumentId.ToString(),
                ["index"] = chunk.ChunkIndex.ToString()
            }),
            Vectors    = vectors
        }, ct);

        return node.GUID;
    }

    public async Task LinkChunkToDocumentAsync(Guid chunkNodeId, Guid docNodeId, CancellationToken ct = default)
    {
        await _client.Edge.CreateAsync(new Edge
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            From       = docNodeId,
            To         = chunkNodeId,
            Name       = LabelEdge
        }, ct);
    }

    // ── Vector Search ─────────────────────────────────────────

    public async Task<List<RetrievedChunk>> VectorSearchAsync(
        float[] queryEmbedding,
        string? domain,
        int topK,
        float minScore,
        CancellationToken ct = default)
    {
        var labels = new List<string> { LabelChunk };
        if (!string.IsNullOrEmpty(domain)) labels.Add(domain);

        var results = new List<VectorSearchResult>();
        await foreach (var r in _client.Vector.SearchAsync(new VectorSearchRequest
        {
            TenantGUID  = _tenant.GUID,
            GraphGUID   = _graph.GUID,
            Domain      = VectorSearchDomainEnum.Node,
            SearchType  = VectorSearchTypeEnum.CosineSimilarity,
            Labels      = labels,
            Embeddings  = queryEmbedding.ToList(),
            TopK        = topK,
            MinimumScore = minScore
        }, ct))
        {
            results.Add(r);
        }

        return results
            .OrderByDescending(r => r.Score)
            .Select(r =>
            {
                try
                {
                    var data = JsonSerializer.Deserialize<JsonElement>(
                        JsonSerializer.Serialize(r.Node?.Data));
                    return new RetrievedChunk
                    {
                        ChunkId     = data.TryGetProperty("Id", out var id)
                                        ? Guid.Parse(id.GetString()!) : Guid.Empty,
                        DocumentId  = data.TryGetProperty("DocumentId", out var did)
                                        ? Guid.Parse(did.GetString()!) : Guid.Empty,
                        Content     = data.TryGetProperty("Content", out var c)
                                        ? c.GetString() ?? "" : "",
                        Score       = r.Score,
                        SourceTitle = r.Node?.Name ?? "",
                        SourceUrl   = ""
                    };
                }
                catch
                {
                    return null!;
                }
            })
            .Where(c => c != null)
            .ToList();
    }

    // ── Graph Context ─────────────────────────────────────────

    public async Task<List<GraphContext>> GraphContextAsync(
        Guid documentNodeId, int depth = 2, CancellationToken ct = default)
    {
        var ctx = new List<GraphContext>();
        var routes = new List<RouteDetail>();

        // Traverse neighbours up to depth
        await foreach (var route in _client.Routes.GetRoutesAsync(
            SearchTypeEnum.BreadthFirstSearch,
            _tenant.GUID, _graph.GUID,
            documentNodeId, null, depth, ct: ct))
        {
            routes.Add(route);
        }

        foreach (var route in routes.Take(20))
        {
            if (route.Edges == null) continue;
            var node = route.Edges.LastOrDefault()?.ToNode;
            if (node == null) continue;

            ctx.Add(new GraphContext
            {
                NodeId = node.GUID,
                Name   = node.Name ?? "",
                Type   = node.Labels?.FirstOrDefault() ?? "",
                Data   = node.Data,
                Edges  = route.Edges.Select(e => new GraphEdgeInfo
                {
                    EdgeId   = e.Edge?.GUID ?? Guid.Empty,
                    Label    = e.Edge?.Name ?? "",
                    ToNodeId = e.ToNode?.GUID ?? Guid.Empty,
                    ToName   = e.ToNode?.Name ?? ""
                }).ToList()
            });
        }

        return ctx;
    }

    // ── Document Status ───────────────────────────────────────

    public async Task<List<SourceDocument>> GetDocumentsByStatusAsync(
        DocumentStatus status, int limit = 100, CancellationToken ct = default)
    {
        var docs = new List<SourceDocument>();
        await foreach (var node in _client.Node.EnumerateAsync(new EnumerationRequest
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            Labels     = new List<string> { LabelDoc },
            Tags       = BuildTags(new Dictionary<string, string> { ["status"] = status.ToString() }),
            MaxResults = limit
        }, ct))
        {
            try
            {
                var data = JsonSerializer.Deserialize<SourceDocument>(
                    JsonSerializer.Serialize(node.Data));
                if (data != null)
                {
                    data.LiteGraphNodeId = node.GUID;
                    docs.Add(data);
                }
            }
            catch { /* skip malformed */ }
        }
        return docs;
    }

    public async Task UpdateDocumentStatusAsync(
        Guid docId, DocumentStatus status, string? error = null, CancellationToken ct = default)
    {
        await foreach (var node in _client.Node.EnumerateAsync(new EnumerationRequest
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            Labels     = new List<string> { LabelDoc },
            Tags       = BuildTags(new Dictionary<string, string> { ["docId"] = docId.ToString() }),
            MaxResults = 1
        }, ct))
        {
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node.Data));
                node.Tags = BuildTags(new Dictionary<string, string>
                {
                    ["status"] = status.ToString(),
                    ["docId"]  = docId.ToString()
                });
                await _client.Node.UpdateAsync(node, ct);
            }
            catch { }
            break;
        }
    }

    public async Task<bool> DocumentExistsByHashAsync(string contentHash, CancellationToken ct = default)
    {
        await foreach (var _ in _client.Node.EnumerateAsync(new EnumerationRequest
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            Labels     = new List<string> { LabelDoc },
            Tags       = BuildTags(new Dictionary<string, string> { ["hash"] = contentHash }),
            MaxResults = 1
        }, ct))
        {
            return true;
        }
        return false;
    }

    // ── Metrics ────────────────────────────────────────────────

    public async Task<IngestionMetrics> GetIngestionMetricsAsync(CancellationToken ct = default)
    {
        var metrics = new IngestionMetrics();
        var byStatus = new Dictionary<string, int>();
        var bySource = new Dictionary<string, int>();

        await foreach (var node in _client.Node.EnumerateAsync(new EnumerationRequest
        {
            TenantGUID   = _tenant.GUID,
            GraphGUID    = _graph.GUID,
            Labels       = new List<string> { LabelDoc },
            MaxResults   = 10_000
        }, ct))
        {
            metrics.TotalDocuments++;
            var tags = node.Tags;
            if (tags != null)
            {
                var status = tags["status"] ?? "";
                byStatus.TryGetValue(status, out var sc); byStatus[status] = sc + 1;
                var src = tags["source"] ?? "";
                bySource.TryGetValue(src, out var ss); bySource[src] = ss + 1;
            }
        }

        long chunkCount = 0;
        await foreach (var _ in _client.Node.EnumerateAsync(new EnumerationRequest
        {
            TenantGUID = _tenant.GUID,
            GraphGUID  = _graph.GUID,
            Labels     = new List<string> { LabelChunk },
            MaxResults = 1_000_000
        }, ct))
        { chunkCount++; }

        metrics.TotalChunks      = chunkCount;
        metrics.IndexedDocuments = byStatus.GetValueOrDefault("Indexed");
        metrics.FailedDocuments  = byStatus.GetValueOrDefault("Failed");
        metrics.PendingDocuments = byStatus.GetValueOrDefault("Pending");
        metrics.BySource         = bySource;

        return metrics;
    }

    // ── Helpers ────────────────────────────────────────────────

    private async Task<Node?> TryGetNodeAsync(Guid nodeId, CancellationToken ct)
    {
        try { return await _client.Node.ReadAsync(_tenant.GUID, _graph.GUID, nodeId, ct); }
        catch { return null; }
    }

    private static NameValueCollection BuildTags(Dictionary<string, string> tags)
    {
        var nvc = new NameValueCollection();
        foreach (var kv in tags) nvc[kv.Key] = kv.Value;
        return nvc;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d) await d.DisposeAsync();
    }
}
