using LiteGraph;
using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Collections.Specialized;

namespace SharePointRag.Core.Services;

/// <summary>
/// LiteGraph-backed vector store scoped to a single (system, data source) pair.
///
/// Storage model:
///   Tenant  "RAGSystem"
///     └─ Graph  "{systemName}__{dataSourceName}"   ← one per (system × source)
///          └─ Nodes  (one per DocumentChunk)
///               Tags:    SourceId, DataSourceName, ConnectorType, ChunkIndex, TotalChunks
///               Data:    { Title, Url, Author, LastModified, Content, Metadata }
///               Vectors: [{ Model, Dimensionality, Content, Vectors=[float…] }]
///
/// Why one graph per (system, data source):
///   - SharePoint nodes carry LibraryPath, DriveItemId in their Metadata
///   - SQL nodes carry arbitrary column values
///   - Deltek nodes carry EntityType, ProjectNumber, ClientName etc.
///   - Excel nodes carry spreadsheet column names as keys
///   Keeping them in separate graphs means metadata schemas never collide.
///   A system-level composite store fans search across all source graphs.
/// </summary>
public sealed class LiteGraphVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly LiteGraphOptions              _opts;
    private readonly int                           _dims;
    private readonly ILogger<LiteGraphVectorStore> _logger;

    // The graph name is "{systemName}__{dataSourceName}"
    private readonly string _graphName;

    private LiteGraphClient? _client;
    private Guid             _tenantGuid;
    private Guid             _graphGuid;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public string SystemName     { get; }
    public string DataSourceName { get; }

    public LiteGraphVectorStore(
        string systemName,
        string dataSourceName,
        LiteGraphOptions opts,
        AzureOpenAIOptions embOpts,
        ILogger<LiteGraphVectorStore> logger)
    {
        SystemName     = systemName;
        DataSourceName = dataSourceName;
        _graphName     = $"{systemName}__{dataSourceName}";
        _opts          = opts;
        _dims          = embOpts.EmbeddingDimensions;
        _logger        = logger;
    }

    private async Task EnsureInitAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;

            var dbDir = Path.GetDirectoryName(_opts.DatabasePath);
            if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

            _client = new LiteGraphClient(new SqliteRepository(_opts.DatabasePath));
            _client.InitializeRepository();

            // Tenant
            TenantMetadata? tenant = null;
            await foreach (var t in _client.Tenant.ReadMany())
                if (t.Name == _opts.TenantName) { tenant = t; break; }

            tenant ??= await _client.Tenant.Create(new TenantMetadata { Name = _opts.TenantName });
            _tenantGuid = tenant.GUID;

            // Graph — one per (system, data source)
            Graph? graph = null;
            await foreach (var g in _client.Graph.ReadMany(_tenantGuid))
                if (g.Name == _graphName) { graph = g; break; }

            graph ??= await _client.Graph.Create(
                new Graph { TenantGUID = _tenantGuid, Name = _graphName });
            _graphGuid = graph.GUID;

            _initialised = true;
            _logger.LogInformation(
                "[LiteGraph] Ready. System='{Sys}' DataSource='{DS}' Graph='{G}'",
                SystemName, DataSourceName, _graphName);
        }
        finally { _initLock.Release(); }
    }

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        return true;
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        _logger.LogDebug("[LiteGraph] Graph '{G}' verified.", _graphName);
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

            // Store all source-specific metadata in Node.Data so each data source
            // can have a completely different set of fields without schema conflicts.
            var data = new ChunkData(
                c.Title,
                c.Url,
                c.Author,
                c.LastModified.ToUnixTimeSeconds(),
                c.Content,
                c.Metadata);   // ← full per-source metadata dict preserved as-is

            await _client!.Node.Create(new Node
            {
                TenantGUID = _tenantGuid,
                GraphGUID  = _graphGuid,
                Name       = c.Id,
                Tags       = tags,
                Data       = data,
                Vectors    = [new VectorMetadata
                {
                    Model          = _opts.EmbeddingModel,
                    Dimensionality = _dims,
                    Content        = c.Content,
                    Vectors        = [.. c.Embedding]
                }]
            });
        }

        _logger.LogDebug("[LiteGraph] Upserted {N} nodes into graph '{G}'.", list.Count, _graphName);
    }

    public async Task DeleteBySourceIdAsync(
        string sourceId, string dataSourceName, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);

        var toDelete = new List<Guid>();
        var req = new EnumerationRequest
        {
            TenantGUID = _tenantGuid,
            Tags       = new NameValueCollection
            {
                ["SourceId"]       = sourceId,
                ["DataSourceName"] = dataSourceName
            },
            MaxResults = 1000
        };

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
                _logger.LogWarning(ex, "[LiteGraph] Could not delete node {G} in '{Graph}'",
                    guid, _graphName);
            }
        }

        if (toDelete.Count > 0)
            _logger.LogDebug("[LiteGraph] Deleted {N} nodes for source '{S}' in graph '{G}'",
                toDelete.Count, sourceId, _graphName);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, double minScore, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);

        var req = new VectorSearchRequest
        {
            TenantGUID   = _tenantGuid,
            GraphGUID    = _graphGuid,
            Domain       = VectorSearchDomainEnum.Node,
            SearchType   = VectorSearchTypeEnum.CosineSimilarity,
            Vectors      = [.. queryVector],
            TopK         = topK,
            MinimumScore = minScore
        };

        var results = new List<RetrievedChunk>();
        await foreach (var sr in _client!.Vector.Search(req).WithCancellation(ct))
        {
            double score = Math.Round(sr.Score, 4);
            if (score < minScore) continue;
            results.Add(new RetrievedChunk(NodeToChunk(sr.Node), score));
        }

        return results;
    }

    public async Task<int> GetNodeCountAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        var stats = await _client!.Graph.GetStatistics(_tenantGuid, _graphGuid);
        return (int)(stats?.Nodes ?? 0);
    }

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

    public ValueTask DisposeAsync()
    {
        (_client as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ChunkData(
        string Title, string Url, string? Author,
        long LastModifiedUnix, string Content,
        Dictionary<string, string> Metadata);
}
