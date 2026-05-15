using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Connectors;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Central runtime registry — source-agnostic, storage-backend-agnostic.
///
/// Per-source store isolation:
///   Each data source within a RAG system gets its own isolated IVectorStore.
///   This means SharePoint nodes, SQL rows, Deltek records, and Excel cells each
///   live in their own LiteGraph graph / Chroma collection with their own metadata
///   schema — they never pollute each other's fields.
///
/// Access patterns:
///   GetVectorStore(systemName)             → CompositeVectorStore (fans across all source stores)
///   GetVectorStore(systemName, sourceName) → isolated per-source IVectorStore
///   GetPipeline(systemName)               → IndexingPipeline (each connector writes to its own store)
/// </summary>
public sealed class LibraryRegistry : ILibraryRegistry
{
    private readonly RagRegistryOptions _config;
    private readonly VectorStoreOptions _vsOpts;

    // Per-source store: (systemName, dataSourceName) → IVectorStore
    private readonly Dictionary<(string Sys, string DS), IVectorStore> _sourceStores = new();

    // Composite per-system store (fans across all source stores for the system)
    private readonly Dictionary<string, IVectorStore>      _systemStores = new();
    private readonly Dictionary<string, IIndexingPipeline> _pipelines    = new();
    private readonly Dictionary<string, IIndexStateStore>  _states       = new();
    private readonly Dictionary<string, IDataSourceConnector> _connectors = new();

    public IReadOnlyList<string> SystemNames     { get; }
    public IReadOnlyList<string> DataSourceNames { get; }

    public LibraryRegistry(
        IOptions<RagRegistryOptions>    registryOpts,
        IOptions<VectorStoreOptions>    vsOpts,
        IVectorStoreFactory             vectorStoreFactory,
        IOptions<LiteGraphOptions>      liteGraphOpts,
        IOptions<AzureOpenAIOptions>    aoaiOpts,
        IConnectorRegistry              connectorRegistry,
        ITextExtractor                  extractor,
        ITextChunker                    chunker,
        IEmbeddingService               embedder,
        ILogger<LibraryRegistry>        logger,
        ILogger<CompositeVectorStore>   compositeLogger,
        ILogger<JsonFileIndexStateStore> stateLogger,
        ILogger<IndexingPipeline>       pipelineLogger)
    {
        _config = registryOpts.Value;
        _vsOpts = vsOpts.Value;

        logger.LogInformation("Vector store backend: {Provider}", _vsOpts.Provider);

        // ── 1. Build one connector per writable data source ──────────────────
        // ReadOnly sources skip connector creation — they are indexed externally.
        // Their vector stores are still opened so searches work normally.
        foreach (var ds in _config.DataSources)
        {
            if (ds.ReadOnly)
            {
                logger.LogInformation(
                    "Data source '{Name}' ({Type}) is ReadOnly — skipping connector, pipeline will not ingest it",
                    ds.Name, ds.Type);
            }
            else
            {
                _connectors[ds.Name] = connectorRegistry.Resolve(ds);
                logger.LogInformation("Registered data source '{Name}' ({Type})", ds.Name, ds.Type);
            }
        }

        // ── 2. State store root (always JSON on disk, same path regardless of backend) ─
        var stateDir = Path.GetDirectoryName(liteGraphOpts.Value.DatabasePath)
                       ?? AppContext.BaseDirectory;

        // ── 3. Build per-source stores + composite store + pipeline per system ─
        foreach (var sys in _config.Systems)
        {
            // Validate sources
            foreach (var dsName in sys.DataSourceNames)
                if (!_connectors.ContainsKey(dsName))
                    throw new InvalidOperationException(
                        $"RAG system '{sys.Name}' references unknown data source '{dsName}'. " +
                        $"Available: {string.Join(", ", _connectors.Keys)}");

            // Build one isolated store per data source in this system
            var sourceStoreMap = new Dictionary<string, IVectorStore>();
            foreach (var dsName in sys.DataSourceNames)
            {
                var ds    = _config.DataSources.First(d => d.Name == dsName);
                var store = vectorStoreFactory.Create(sys, ds);
                _sourceStores[(sys.Name, dsName)] = store;
                sourceStoreMap[dsName] = store;

                logger.LogInformation(
                    "Registered vector store for system '{Sys}' / source '{DS}' [{Backend}]",
                    sys.Name, dsName, _vsOpts.Provider);
            }

            // Composite wraps all per-source stores for this system
            var composite = new CompositeVectorStore(sys.Name, sourceStoreMap, compositeLogger);
            _systemStores[sys.Name] = composite;

            var state = new JsonFileIndexStateStore(sys.Name, stateDir, stateLogger);
            _states[sys.Name] = state;

            // Pipeline: only writable (non-ReadOnly) sources are indexed.
            // ReadOnly sources' stores are open for search but never written to by this instance.
            var writableSources = sys.DataSourceNames
                .Where(n => !_config.DataSources.First(d => d.Name == n).ReadOnly)
                .Select(n =>
                {
                    var ds        = _config.DataSources.First(d => d.Name == n);
                    var connector = _connectors[n];
                    var store     = sourceStoreMap[n];
                    return (ds, connector, store);
                }).ToList();

            var readOnlySourceNames = sys.DataSourceNames
                .Where(n => _config.DataSources.First(d => d.Name == n).ReadOnly)
                .ToList();

            if (readOnlySourceNames.Count > 0)
                logger.LogInformation(
                    "System '{Sys}': {N} read-only source(s) [{DS}] — stores opened for search only",
                    sys.Name, readOnlySourceNames.Count, string.Join(", ", readOnlySourceNames));

            _pipelines[sys.Name] = new PerSourceIndexingPipeline(
                sys, writableSources, extractor, chunker, embedder, state, pipelineLogger);

            logger.LogInformation(
                "Registered RAG system '{Sys}' ← [{Sources}]",
                sys.Name, string.Join(", ", sys.DataSourceNames));
        }

        SystemNames     = [.. _config.Systems.Select(s => s.Name)];
        DataSourceNames = [.. _config.DataSources.Select(d => d.Name)];
    }

    public RagSystemDefinition  GetSystem(string name) =>
        _config.Systems.FirstOrDefault(s => s.Name == name)
        ?? throw new KeyNotFoundException($"RAG system '{name}' not found.");

    public DataSourceDefinition GetDataSource(string name) =>
        _config.DataSources.FirstOrDefault(d => d.Name == name)
        ?? throw new KeyNotFoundException($"Data source '{name}' not found.");

    /// <summary>Returns the composite store that fans across all source stores in the system.</summary>
    public IVectorStore GetVectorStore(string systemName) =>
        _systemStores.TryGetValue(systemName, out var s) ? s
        : throw new KeyNotFoundException($"No vector store for system '{systemName}'.");

    /// <summary>Returns the isolated per-source store for a specific (system, data source) pair.</summary>
    public IVectorStore GetVectorStore(string systemName, string dataSourceName) =>
        _sourceStores.TryGetValue((systemName, dataSourceName), out var s) ? s
        : throw new KeyNotFoundException(
            $"No vector store for system '{systemName}' / source '{dataSourceName}'.");

    public IIndexingPipeline    GetPipeline(string systemName) =>
        _pipelines.TryGetValue(systemName, out var p) ? p
        : throw new KeyNotFoundException($"No pipeline for system '{systemName}'.");

    public IIndexStateStore     GetStateStore(string systemName) =>
        _states.TryGetValue(systemName, out var s) ? s
        : throw new KeyNotFoundException($"No state store for system '{systemName}'.");

    public IDataSourceConnector GetConnector(string dataSourceName) =>
        _connectors.TryGetValue(dataSourceName, out var c) ? c
        : throw new KeyNotFoundException($"No connector for data source '{dataSourceName}'.");

    public async Task<List<RagSystemStatus>> GetAllStatusAsync(CancellationToken ct = default)
    {
        var result = new List<RagSystemStatus>();
        foreach (var sys in _config.Systems)
        {
            var state = _states[sys.Name];
            bool healthy;
            try { healthy = await _systemStores[sys.Name].IndexExistsAsync(ct); }
            catch { healthy = false; }

            var dsStatuses = new List<DataSourceStatus>();
            foreach (var dsName in sys.DataSourceNames)
            {
                var ds        = GetDataSource(dsName);
                var connector = _connectors[dsName];
                var count     = await state.GetIndexedRecordCountAsync(dsName, ct);
                var last      = await state.GetLastFullIndexTimeAsync(dsName, ct);

                string connInfo;
                bool   isReachable;
                string? connError;

                if (ds.ReadOnly)
                {
                    // No connector for read-only sources — report that clearly
                    connInfo   = "ReadOnly — externally managed, no connector in this instance";
                    isReachable = true;   // store is open; we just don't own ingestion
                    connError  = null;
                }
                else if (_connectors.TryGetValue(dsName, out var connector))
                {
                    try
                    {
                        connInfo   = await connector.TestConnectionAsync(ct);
                        isReachable = !connInfo.StartsWith("Error:") && !connInfo.StartsWith("Connection failed:");
                        connError  = isReachable ? null : connInfo;
                    }
                    catch (Exception ex)
                    {
                        connInfo   = $"Error: {ex.Message}";
                        isReachable = false;
                        connError  = connInfo;
                    }
                }
                else
                {
                    connInfo   = "No connector registered";
                    isReachable = false;
                    connError  = connInfo;
                }

                dsStatuses.Add(new DataSourceStatus(
                    DataSourceName:     dsName,
                    ConnectorType:      ds.Type.ToString(),
                    ConnectionInfo:     connInfo,
                    IsReachable:        isReachable,
                    IndexedRecordCount: count,
                    LastFullIndex:      last,
                    LastDeltaIndex:     null,
                    ConnectionError:    connError,
                    ReadOnly:           ds.ReadOnly));
            }

            result.Add(new RagSystemStatus(sys.Name, sys.Description, healthy, dsStatuses));
        }
        return result;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  PER-SOURCE INDEXING PIPELINE
//  Replaces IndexingPipeline in the registry. Each connector is bound to its
//  own isolated IVectorStore, so writes go to the right per-source store.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Indexing pipeline where each data source connector is bound directly to its
/// own isolated IVectorStore. Writes from SharePointConnector go to the SharePoint
/// graph; writes from SqlConnector go to the SQL graph — never mixed.
/// </summary>
file sealed class PerSourceIndexingPipeline : IIndexingPipeline
{
    private readonly RagSystemDefinition _system;
    private readonly IReadOnlyList<(DataSourceDefinition Def, IDataSourceConnector Connector, IVectorStore Store)> _sources;
    private readonly ITextExtractor       _extractor;
    private readonly ITextChunker         _chunker;
    private readonly IEmbeddingService    _embedder;
    private readonly IIndexStateStore     _stateStore;
    private readonly ILogger              _logger;

    public string SystemName => _system.Name;

    public PerSourceIndexingPipeline(
        RagSystemDefinition system,
        IEnumerable<(DataSourceDefinition, IDataSourceConnector, IVectorStore)> sources,
        ITextExtractor extractor,
        ITextChunker chunker,
        IEmbeddingService embedder,
        IIndexStateStore stateStore,
        ILogger logger)
    {
        _system     = system;
        _sources    = [.. sources];
        _extractor  = extractor;
        _chunker    = chunker;
        _embedder   = embedder;
        _stateStore = stateStore;
        _logger     = logger;
    }

    public async Task RunFullIndexAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[{Sys}] FULL index — {N} source(s)", _system.Name, _sources.Count);

        foreach (var (def, connector, store) in _sources)
        {
            await store.CreateIndexIfNotExistsAsync(ct);
            _logger.LogInformation("[{Sys}] Full-indexing '{Src}' ({Type})",
                _system.Name, connector.DataSourceName, connector.ConnectorType);
            await ProcessSourceAsync(connector, def, store, connector.GetRecordsAsync(ct), ct);
            await _stateStore.SetLastFullIndexTimeAsync(connector.DataSourceName, DateTimeOffset.UtcNow, ct);
        }

        _logger.LogInformation("[{Sys}] Full index complete.", _system.Name);
    }

    public async Task RunDeltaIndexAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[{Sys}] DELTA index — {N} source(s)", _system.Name, _sources.Count);

        foreach (var (def, connector, store) in _sources)
        {
            var since = await _stateStore.GetLastFullIndexTimeAsync(connector.DataSourceName, ct)
                        ?? DateTimeOffset.UtcNow.AddDays(-7);

            IAsyncEnumerable<SourceRecord> records = def.DeltaSupported
                ? connector.GetModifiedRecordsAsync(since, ct)
                : connector.GetRecordsAsync(ct);

            _logger.LogInformation("[{Sys}] Delta '{Src}' since {Since}", _system.Name, connector.DataSourceName, since);
            await ProcessSourceAsync(connector, def, store, records, ct);
        }

        _logger.LogInformation("[{Sys}] Delta index complete.", _system.Name);
    }

    private async Task ProcessSourceAsync(
        IDataSourceConnector           connector,
        DataSourceDefinition           def,
        IVectorStore                   store,
        IAsyncEnumerable<SourceRecord> records,
        CancellationToken              ct)
    {
        var parallelism = Math.Max(1, def.CrawlParallelism);
        var semaphore   = new SemaphoreSlim(parallelism, parallelism);
        var tasks       = new List<Task>();

        await foreach (var record in records.WithCancellation(ct))
        {
            await semaphore.WaitAsync(ct);
            var r = record;
            tasks.Add(Task.Run(async () =>
            {
                try   { await IndexRecordAsync(connector, store, r, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Sys}/{Src}] Failed to index '{Id}'",
                        _system.Name, connector.DataSourceName, r.Id);
                    await _stateStore.SaveAsync(new IndexingRecord
                    {
                        SourceId       = r.Id,
                        DataSourceName = r.DataSourceName,
                        RagSystemName  = _system.Name,
                        Title          = r.Title,
                        Status         = IndexingStatus.Failed,
                        ErrorMessage   = ex.Message,
                        LastIndexed    = DateTimeOffset.UtcNow
                    }, ct);
                }
                finally { semaphore.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task IndexRecordAsync(
        IDataSourceConnector connector,
        IVectorStore         store,
        SourceRecord         record,
        CancellationToken    ct)
    {
        var existing = await _stateStore.GetAsync(record.Id, record.DataSourceName, ct);
        if (existing?.Status == IndexingStatus.Indexed && existing.LastIndexed >= record.LastModified)
        {
            _logger.LogDebug("[{Sys}/{Src}] Skipping unchanged '{Id}'",
                _system.Name, record.DataSourceName, record.Id);
            return;
        }

        var text = record.Content;
        if (string.IsNullOrWhiteSpace(text) && record.RawContent != null)
            text = await _extractor.ExtractAsync(record.RawContent, record.MimeType, record.Title, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            await _stateStore.SaveAsync(new IndexingRecord
            {
                SourceId = record.Id, DataSourceName = record.DataSourceName,
                RagSystemName = _system.Name, Title = record.Title,
                Status = IndexingStatus.Skipped, LastIndexed = DateTimeOffset.UtcNow
            }, ct);
            return;
        }

        var textChunks = _chunker.Chunk(text);
        var embeddings = await _embedder.EmbedBatchAsync(textChunks, ct);

        var enrichedMeta = new Dictionary<string, string>(record.Metadata)
            { ["ConnectorType"] = connector.ConnectorType.ToString() };

        var chunks = textChunks.Select((t, i) => new DocumentChunk
        {
            SourceId       = record.Id,
            DataSourceName = record.DataSourceName,
            Title          = record.Title,
            Url            = record.Url,
            Author         = record.Author,
            LastModified   = record.LastModified,
            Content        = t,
            ChunkIndex     = i,
            TotalChunks    = textChunks.Count,
            Metadata       = enrichedMeta,
            Embedding      = embeddings[i]
        }).ToList();

        // Delete from this source's own store, then upsert
        await store.DeleteBySourceIdAsync(record.Id, record.DataSourceName, ct);
        await store.UpsertAsync(chunks, ct);

        await _stateStore.SaveAsync(new IndexingRecord
        {
            SourceId = record.Id, DataSourceName = record.DataSourceName,
            RagSystemName = _system.Name, Title = record.Title,
            Status = IndexingStatus.Indexed, ChunkCount = chunks.Count,
            LastIndexed = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("[{Sys}/{Src}] Indexed '{Title}' → {N} chunks",
            _system.Name, record.DataSourceName, record.Title, chunks.Count);
    }
}
