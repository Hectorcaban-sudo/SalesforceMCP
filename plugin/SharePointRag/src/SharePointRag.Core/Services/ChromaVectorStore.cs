using ChromaDB.Client;
using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SharePointRag.Core.Services;

/// <summary>
/// ChromaDB-backed vector store.
///
/// Storage model (one Chroma server, one collection per RAG system):
///   Collection  "{CollectionPrefix}{systemName}"   e.g. "rag_General"
///     Documents  (one per DocumentChunk)
///       id:        chunk.Id
///       embedding: float[]
///       document:  chunk.Content
///       metadata:  {
///                    sourceId, dataSourceName, connectorType,
///                    title, url, author, lastModifiedUnix,
///                    chunkIndex, totalChunks,
///                    + all chunk.Metadata entries
///                  }
///
/// Why one collection per system:
///   - Chroma collections are isolated query scopes — same as LiteGraph graphs
///   - Metadata filters provide sub-collection filtering for delta deletes
///   - No cross-system contamination possible
///
/// Delta delete strategy:
///   ChromaDB supports server-side `where` metadata filters in Get() and Delete().
///   We filter by sourceId + dataSourceName metadata fields and delete matching IDs
///   in a single round-trip — far more efficient than the LiteGraph paginate+delete loop.
///
/// Score conversion:
///   Chroma returns distances (0 = identical for cosine, higher = less similar).
///   We convert: score = 1 - (distance / 2) for cosine space so the result is
///   in [0, 1] and directly comparable to LiteGraph cosine similarity scores.
/// </summary>
public sealed class ChromaVectorStore : IVectorStore
{
    private readonly RagSystemDefinition       _system;
    private readonly ChromaOptions             _opts;
    private readonly ILogger<ChromaVectorStore> _logger;

    private ChromaClient?           _client;
    private ChromaCollectionClient? _collection;
    private readonly SemaphoreSlim  _initLock = new(1, 1);
    private bool _initialised;

    public string SystemName => _system.Name;

    // Chroma collection name for this system
    private string CollectionName => $"{_opts.CollectionPrefix}{_system.Name}";

    public ChromaVectorStore(
        RagSystemDefinition        system,
        ChromaOptions              opts,
        ILogger<ChromaVectorStore> logger)
    {
        _system = system;
        _opts   = opts;
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

            var http = BuildHttpClient();
            var cfg  = BuildConfigOptions();

            _client = new ChromaClient(cfg, http);

            // Verify server reachability
            try
            {
                var version = await _client.GetVersion();
                _logger.LogInformation(
                    "[Chroma/{Sys}] Server version: {V}  Collection: {C}",
                    _system.Name, version, CollectionName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot reach ChromaDB at '{_opts.Endpoint}'. " +
                    $"Start the server: docker run -p 8000:8000 chromadb/chroma. " +
                    $"Inner: {ex.Message}", ex);
            }

            // GetOrCreate the collection with cosine distance metadata
            var chromaCollection = await _client.GetOrCreateCollection(
                CollectionName,
                metadata: new Dictionary<string, object>
                {
                    ["hnsw:space"] = _opts.DistanceFunction
                });

            _collection = new ChromaCollectionClient(chromaCollection, cfg, BuildHttpClient());

            _initialised = true;
            _logger.LogInformation(
                "[Chroma/{Sys}] Ready. Collection='{C}'", _system.Name, CollectionName);
        }
        finally { _initLock.Release(); }
    }

    // ── IVectorStore implementation ───────────────────────────────────────────

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        return true;
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        _logger.LogInformation("[Chroma/{Sys}] Collection verified.", _system.Name);
    }

    public async Task UpsertAsync(
        IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        var list = chunks.ToList();
        if (list.Count == 0) return;

        var ids        = new List<string>(list.Count);
        var embeddings = new List<ReadOnlyMemory<float>>(list.Count);
        var documents  = new List<string>(list.Count);
        var metadatas  = new List<Dictionary<string, object>>(list.Count);

        foreach (var c in list)
        {
            if (c.Embedding is null)
                throw new InvalidOperationException(
                    $"Chunk '{c.Id}' has no embedding — embed before upserting.");

            ids.Add(c.Id);
            embeddings.Add(new ReadOnlyMemory<float>(c.Embedding));
            documents.Add(c.Content);

            // Flatten all metadata into Chroma's string-keyed dict
            // Chroma metadata values must be string, int, float, or bool
            var meta = new Dictionary<string, object>
            {
                ["sourceId"]         = c.SourceId,
                ["dataSourceName"]   = c.DataSourceName,
                ["connectorType"]    = c.Metadata.TryGetValue("ConnectorType", out var ct2) ? ct2 : "",
                ["title"]            = c.Title,
                ["url"]              = c.Url,
                ["author"]           = c.Author ?? "",
                ["lastModifiedUnix"] = c.LastModified.ToUnixTimeSeconds(),
                ["chunkIndex"]       = c.ChunkIndex,
                ["totalChunks"]      = c.TotalChunks
            };

            // Merge source connector metadata, prefixed to avoid collisions
            foreach (var kv in c.Metadata)
                meta[$"src_{kv.Key}"] = kv.Value;

            metadatas.Add(meta);
        }

        // Chroma Upsert: add new or update existing by ID
        await _collection!.Upsert(ids, embeddings: embeddings, documents: documents, metadatas: metadatas);

        _logger.LogDebug("[Chroma/{Sys}] Upserted {N} documents.", _system.Name, list.Count);
    }

    public async Task DeleteBySourceIdAsync(
        string sourceId, string dataSourceName, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);

        // Chroma supports server-side where-filter deletes — no pagination needed
        var where = new Dictionary<string, object>
        {
            ["$and"] = new List<object>
            {
                new Dictionary<string, object> { ["sourceId"]       = new Dictionary<string, object> { ["$eq"] = sourceId } },
                new Dictionary<string, object> { ["dataSourceName"] = new Dictionary<string, object> { ["$eq"] = dataSourceName } }
            }
        };

        // First get matching IDs, then delete by ID (Chroma delete requires explicit IDs)
        var existing = await _collection!.Get(
            where: where,
            include: ChromaGetInclude.None);

        if (existing is not null && existing.Ids.Count > 0)
        {
            await _collection!.Delete(existing.Ids);
            _logger.LogDebug("[Chroma/{Sys}] Deleted {N} docs for source '{S}'",
                _system.Name, existing.Ids.Count, sourceId);
        }
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, double minScore, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);

        // Request more than topK to account for minScore filtering
        int nResults = Math.Min(Math.Max(topK * 2, _opts.DefaultTopK), 100);

        var queryResults = await _collection!.Query(
            [new ReadOnlyMemory<float>(queryVector)],
            nResults:     nResults,
            include:      ChromaQueryInclude.Metadatas |
                          ChromaQueryInclude.Documents  |
                          ChromaQueryInclude.Distances);

        if (queryResults is null || queryResults.Count == 0)
            return [];

        var results = new List<RetrievedChunk>();

        // queryResults is a list of result sets (one per query vector).
        // We sent exactly 1 query vector so take index 0.
        foreach (var entry in queryResults[0])
        {
            // Convert Chroma distance to similarity score.
            // For cosine space: distance ∈ [0, 2]; score = 1 - distance/2 → [0, 1]
            double score = _opts.DistanceFunction.ToLowerInvariant() switch
            {
                "cosine" => Math.Round(1.0 - entry.Distance / 2.0, 4),
                "ip"     => Math.Round((double)entry.Distance, 4),  // inner product: higher = better
                _        => Math.Round(1.0 / (1.0 + entry.Distance), 4) // l2: invert
            };

            if (score < minScore) continue;

            var chunk = MetadataToChunk(entry.Id, entry.Document ?? "", entry.Metadata);
            results.Add(new RetrievedChunk(chunk, score));

            if (results.Count >= topK) break;
        }

        _logger.LogDebug("[Chroma/{Sys}] Search returned {N} results.", _system.Name, results.Count);
        return results;
    }

    // ── Chroma metadata → DocumentChunk ──────────────────────────────────────

    private static DocumentChunk MetadataToChunk(
        string id,
        string document,
        IReadOnlyDictionary<string, object>? meta)
    {
        meta ??= new Dictionary<string, object>();

        string GetStr(string key) =>
            meta.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        long GetLong(string key) =>
            meta.TryGetValue(key, out var v) && long.TryParse(v?.ToString(), out var l) ? l : 0;

        int GetInt(string key) =>
            meta.TryGetValue(key, out var v) && int.TryParse(v?.ToString(), out var i) ? i : 0;

        // Reconstruct connector metadata from prefixed keys
        var chunkMeta = meta
            .Where(kv => kv.Key.StartsWith("src_"))
            .ToDictionary(kv => kv.Key[4..], kv => kv.Value?.ToString() ?? "");

        // Also restore ConnectorType without prefix for downstream consumers
        if (!string.IsNullOrEmpty(GetStr("connectorType")))
            chunkMeta["ConnectorType"] = GetStr("connectorType");

        return new DocumentChunk
        {
            Id             = id,
            SourceId       = GetStr("sourceId"),
            DataSourceName = GetStr("dataSourceName"),
            Title          = GetStr("title"),
            Url            = GetStr("url"),
            Author         = GetStr("author") is { Length: > 0 } a ? a : null,
            LastModified   = DateTimeOffset.FromUnixTimeSeconds(GetLong("lastModifiedUnix")),
            Content        = document,
            ChunkIndex     = GetInt("chunkIndex"),
            TotalChunks    = GetInt("totalChunks"),
            Metadata       = chunkMeta
        };
    }

    // ── Status helper ─────────────────────────────────────────────────────────

    public async Task<(int Count, string ServerVersion)> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        try
        {
            var version = await _client!.GetVersion();
            var count   = await _collection!.Count();
            return (count, version ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chroma/{Sys}] Could not get stats", _system.Name);
            return (0, "unreachable");
        }
    }

    // ── HTTP client factory ───────────────────────────────────────────────────

    private HttpClient BuildHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_opts.TimeoutSeconds)
        };

        if (!string.IsNullOrEmpty(_opts.ApiKey))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        return http;
    }

    private ChromaConfigurationOptions BuildConfigOptions()
    {
        var uri = _opts.Endpoint.TrimEnd('/');
        // ChromaConfigurationOptions expects the full API base URI
        return new ChromaConfigurationOptions(uri: uri + "/");
    }
}
