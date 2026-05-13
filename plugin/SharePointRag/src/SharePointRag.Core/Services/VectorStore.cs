using LiteGraph;
using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Collections.Specialized;

namespace SharePointRag.Core.Services;

/// <summary>
/// LiteGraph-backed persistent vector + graph store.
///
/// Storage model (one SQLite file, shared across all systems):
///   Tenant  "RAGSystem"
///     └─ Graph  "{systemName}"          ← one per RAG system, fully isolated
///          └─ Nodes  (one per DocumentChunk)
///               Tags:    SourceId, DataSourceName, ConnectorType, ChunkIndex, TotalChunks
///               Data:    { Title, Url, Author, LastModified, Content, Metadata }
///               Vectors: [{ Model, Dimensionality, Content=chunkText, Vectors=[float…] }]
///
/// Why one file / many graphs vs one file per system:
///   - Single file = single backup, atomic migrations
///   - Graph isolation = queries never cross system boundaries
///   - LiteGraph Tags allow sub-graph filtering without extra SQL
///
/// Delta delete strategy:
///   Enumerate nodes by Tags["SourceId"] + Tags["DataSourceName"] → delete each.
///   LiteGraph v6 has no bulk-delete-by-tag API; we enumerate then delete individually.
/// </summary>
public sealed class LiteGraphVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly RagSystemDefinition         _system;
    private readonly LiteGraphOptions            _opts;
    private readonly int                         _dims;
    private readonly ILogger<LiteGraphVectorStore> _logger;

    // LiteGraph state — lazily initialised
    private LiteGraphClient? _client;
    private Guid             _tenantGuid;
    private Guid             _graphGuid;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public string SystemName => _system.Name;

    public LiteGraphVectorStore(
        RagSystemDefinition system,
        LiteGraphOptions opts,
        AzureOpenAIOptions embOpts,
        ILogger<LiteGraphVectorStore> logger)
    {
        _system = system;
        _opts   = opts;
        _dims   = embOpts.EmbeddingDimensions;
        _logger = logger;
    }

    // ── Lazy initialisation ───────────────────────────────────────────────────

    private async Task EnsureInitAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;

            // Open / create the shared SQLite database
            var dbDir = Path.GetDirectoryName(_opts.DatabasePath);
            if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

            _client = new LiteGraphClient(new SqliteRepository(_opts.DatabasePath));
            _client.InitializeRepository();

            // ── Tenant: find or create ────────────────────────────────────────
            TenantMetadata? tenant = null;
            await foreach (var t in _client.Tenant.ReadMany())
            {
                if (t.Name == _opts.TenantName) { tenant = t; break; }
            }
            if (tenant is null)
            {
                tenant = await _client.Tenant.Create(
                    new TenantMetadata { Name = _opts.TenantName });
                _logger.LogInformation("[LiteGraph] Created tenant '{T}'", _opts.TenantName);
            }
            _tenantGuid = tenant.GUID;

            // ── Graph: find or create one per RAG system ──────────────────────
            Graph? graph = null;
            await foreach (var g in _client.Graph.ReadMany(_tenantGuid))
            {
                if (g.Name == _system.Name) { graph = g; break; }
            }
            if (graph is null)
            {
                graph = await _client.Graph.Create(
                    new Graph { TenantGUID = _tenantGuid, Name = _system.Name });
                _logger.LogInformation("[LiteGraph] Created graph '{G}'", _system.Name);
            }
            _graphGuid = graph.GUID;

            _initialised = true;
            _logger.LogInformation(
                "[LiteGraph/{Sys}] Ready. Tenant={T} Graph={G} DB={DB}",
                _system.Name, _tenantGuid, _graphGuid, _opts.DatabasePath);
        }
        finally { _initLock.Release(); }
    }

    // ── IVectorStore implementation ───────────────────────────────────────────

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        return true; // Graph was created (or already existed) during init
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct); // idempotent — init creates tenant+graph
        _logger.LogInformation("[LiteGraph/{Sys}] Graph verified.", _system.Name);
    }

    public async Task UpsertAsync(
        IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        var list = chunks.ToList();
        if (list.Count == 0) return;

        foreach (var c in list)
        {
            if (c.Embedding is null)
                throw new InvalidOperationException(
                    $"Chunk '{c.Id}' has no embedding — embed before upserting.");

            var tags = new NameValueCollection
            {
                ["SourceId"]       = c.SourceId,
                ["DataSourceName"] = c.DataSourceName,
                ["ConnectorType"]  = c.Metadata.TryGetValue("ConnectorType", out var ct2) ? ct2 : "",
                ["ChunkIndex"]     = c.ChunkIndex.ToString(),
                ["TotalChunks"]    = c.TotalChunks.ToString()
            };

            var data = new ChunkData(
                c.Title,
                c.Url,
                c.Author,
                c.LastModified.ToUnixTimeSeconds(),
                c.Content,
                c.Metadata);

            var vectors = new List<VectorMetadata>
            {
                new VectorMetadata
                {
                    Model         = _opts.EmbeddingModel,
                    Dimensionality = _dims,
                    Content       = c.Content,
                    Vectors       = [.. c.Embedding]
                }
            };

            await _client!.Node.Create(new Node
            {
                TenantGUID = _tenantGuid,
                GraphGUID  = _graphGuid,
                Name       = c.Id,      // stable chunk ID as node name
                Tags       = tags,
                Data       = data,
                Vectors    = vectors
            });
        }

        _logger.LogDebug("[LiteGraph/{Sys}] Upserted {N} nodes.", _system.Name, list.Count);
    }

    public async Task DeleteBySourceIdAsync(
        string sourceId, string dataSourceName, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);

        // Find all nodes that belong to this source record
        var filter = new NameValueCollection
        {
            ["SourceId"]       = sourceId,
            ["DataSourceName"] = dataSourceName
        };

        var toDelete = new List<Guid>();
        var req = new EnumerationRequest
        {
            TenantGUID = _tenantGuid,
            Tags       = filter,
            MaxResults = 1000
        };

        // Paginate until end of results
        string? token = null;
        bool done = false;
        while (!done)
        {
            req.ContinuationToken = token;
            var page = await _client!.Node.Enumerate(req);
            foreach (var n in page.Objects) toDelete.Add(n.GUID);
            done  = page.EndOfResults;
            token = page.ContinuationToken?.ToString();
        }

        foreach (var guid in toDelete)
        {
            try { await _client!.Node.Delete(_tenantGuid, _graphGuid, guid); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[LiteGraph/{Sys}] Could not delete node {G}", _system.Name, guid);
            }
        }

        if (toDelete.Count > 0)
            _logger.LogDebug("[LiteGraph/{Sys}] Deleted {N} nodes for source '{S}'",
                _system.Name, toDelete.Count, sourceId);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, double minScore, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);

        var req = new VectorSearchRequest
        {
            TenantGUID    = _tenantGuid,
            GraphGUID     = _graphGuid,
            Domain        = VectorSearchDomainEnum.Node,
            SearchType    = VectorSearchTypeEnum.CosineSimilarity,
            Vectors       = [.. queryVector],
            TopK          = topK,
            MinimumScore  = minScore
        };

        var results = new List<RetrievedChunk>();

        await foreach (var sr in _client!.Vector.Search(req).WithCancellation(ct))
        {
            double score = Math.Round(sr.Score, 4);
            if (score < minScore) continue;

            var chunk = NodeToChunk(sr.Node);
            results.Add(new RetrievedChunk(chunk, score));
        }

        _logger.LogDebug("[LiteGraph/{Sys}] Search returned {N} results.", _system.Name, results.Count);
        return results;
    }

    // ── Node → DocumentChunk mapping ─────────────────────────────────────────

    private static DocumentChunk NodeToChunk(Node node)
    {
        ChunkData? data = null;
        if (node.Data is System.Text.Json.JsonElement je)
            data = je.Deserialize<ChunkData>(new System.Text.Json.JsonSerializerOptions
                { PropertyNameCaseInsensitive = true });
        else if (node.Data is ChunkData cd)
            data = cd;

        var tags = node.Tags ?? new NameValueCollection();

        return new DocumentChunk
        {
            Id             = node.Name ?? node.GUID.ToString(),
            SourceId       = tags["SourceId"]       ?? string.Empty,
            DataSourceName = tags["DataSourceName"] ?? string.Empty,
            Title          = data?.Title            ?? string.Empty,
            Url            = data?.Url              ?? string.Empty,
            Author         = data?.Author,
            LastModified   = data is not null
                             ? DateTimeOffset.FromUnixTimeSeconds(data.LastModifiedUnix)
                             : DateTimeOffset.UtcNow,
            Content        = data?.Content          ?? string.Empty,
            ChunkIndex     = int.TryParse(tags["ChunkIndex"],  out var ci) ? ci : 0,
            TotalChunks    = int.TryParse(tags["TotalChunks"], out var tc) ? tc : 1,
            Metadata       = data?.Metadata         ?? []
        };
    }

    // ── Stats helper (used by registry status) ────────────────────────────────

    public async Task<int> GetNodeCountAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        var stats = await _client!.Graph.GetStatistics(_tenantGuid, _graphGuid);
        return (int)(stats?.Nodes ?? 0);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        (_client as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Inner data record stored in Node.Data ─────────────────────────────────

    private sealed record ChunkData(
        string Title,
        string Url,
        string? Author,
        long LastModifiedUnix,
        string Content,
        Dictionary<string, string> Metadata
    );
}
