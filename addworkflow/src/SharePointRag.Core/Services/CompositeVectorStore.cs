using Microsoft.Extensions.Logging;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Fan-out vector store that wraps one per-source store for each data source
/// assigned to a RAG system.
///
/// Routing rules:
///   UpsertAsync       → routes each chunk to the store matching chunk.DataSourceName
///   DeleteBySourceId  → routes to the store matching the dataSourceName argument
///   SearchAsync       → queries ALL per-source stores in parallel, merges results,
///                       re-ranks by score, trims to topK
///   CreateIndex /     → calls through to every per-source store
///   IndexExists
///
/// Why this design instead of one shared store:
///   - Each data source retains its own metadata schema in isolation
///   - SharePoint nodes have LibraryPath/DriveItemId fields; SQL nodes have column values;
///     Deltek nodes have ProjectNumber/EntityType — they never pollute each other
///   - Per-source stores can be provisioned, queried, or wiped independently
///   - Status API can report accurate per-source node counts
///   - Future: per-source TopK/MinScore tuning without system-level config changes
/// </summary>
public sealed class CompositeVectorStore : IVectorStore
{
    private readonly IReadOnlyDictionary<string, IVectorStore> _stores;   // keyed by dataSourceName
    private readonly ILogger<CompositeVectorStore>             _logger;

    public string SystemName     { get; }
    public string DataSourceName => string.Empty;  // composite — spans all sources

    public CompositeVectorStore(
        string systemName,
        IReadOnlyDictionary<string, IVectorStore> storesByDataSource,
        ILogger<CompositeVectorStore> logger)
    {
        SystemName = systemName;
        _stores    = storesByDataSource;
        _logger    = logger;
    }

    /// <summary>The individual per-source stores that this composite wraps.</summary>
    public IReadOnlyDictionary<string, IVectorStore> SourceStores => _stores;

    // ── Provision ─────────────────────────────────────────────────────────────

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        // All stores must exist for the system to be considered healthy
        foreach (var store in _stores.Values)
            if (!await store.IndexExistsAsync(ct)) return false;
        return _stores.Count > 0;
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        foreach (var (dsName, store) in _stores)
        {
            await store.CreateIndexIfNotExistsAsync(ct);
            _logger.LogDebug("[{Sys}] Provisioned store for data source '{DS}'", SystemName, dsName);
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task UpsertAsync(
        IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        // Group chunks by data source and route to the matching per-source store
        var grouped = chunks.GroupBy(c => c.DataSourceName).ToList();

        foreach (var group in grouped)
        {
            if (_stores.TryGetValue(group.Key, out var store))
            {
                await store.UpsertAsync(group, ct);
            }
            else
            {
                _logger.LogWarning(
                    "[{Sys}] No store found for data source '{DS}' — chunks skipped. " +
                    "Available stores: [{Available}]",
                    SystemName, group.Key, string.Join(", ", _stores.Keys));
            }
        }
    }

    public async Task DeleteBySourceIdAsync(
        string sourceId, string dataSourceName, CancellationToken ct = default)
    {
        if (_stores.TryGetValue(dataSourceName, out var store))
            await store.DeleteBySourceIdAsync(sourceId, dataSourceName, ct);
        else
            _logger.LogWarning("[{Sys}] DeleteBySourceId: no store for '{DS}'", SystemName, dataSourceName);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, double minScore, CancellationToken ct = default)
    {
        if (_stores.Count == 0) return [];

        // Query all per-source stores in parallel
        var tasks = _stores.Select(kv =>
            kv.Value.SearchAsync(queryVector, topK, minScore, ct)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            _logger.LogError(t.Exception,
                                "[{Sys}] Search failed for source '{DS}'", SystemName, kv.Key);
                            return (IReadOnlyList<RetrievedChunk>)[];
                        }
                        return t.Result;
                    }, TaskScheduler.Default)
        ).ToList();

        var allResults = await Task.WhenAll(tasks);

        // Merge and re-rank by descending score, take topK across all sources
        var merged = allResults
            .SelectMany(r => r)
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        _logger.LogDebug(
            "[{Sys}] Composite search: {T} total results from {N} source store(s), returning {K}",
            SystemName, allResults.Sum(r => r.Count), _stores.Count, merged.Count);

        return merged;
    }
}
