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
/// Builds one set of services per RAG system at startup:
///   IDataSourceConnector  per data source  (SharePoint, SQL, Excel, Deltek, Custom…)
///   IVectorStore          per system       (LiteGraph or Chroma — selected by VectorStoreOptions.Provider)
///   IIndexStateStore      per system
///   IIndexingPipeline     per system       (fans over all assigned connectors)
///
/// The vector store backend is selected via VectorStoreOptions.Provider in appsettings.
/// No code changes are needed to switch backends — only a config change.
/// </summary>
public sealed class LibraryRegistry : ILibraryRegistry
{
    private readonly RagRegistryOptions _config;
    private readonly VectorStoreOptions _vsOpts;
    private readonly Dictionary<string, IVectorStore>         _stores     = new();
    private readonly Dictionary<string, IIndexingPipeline>    _pipelines  = new();
    private readonly Dictionary<string, IIndexStateStore>     _states     = new();
    private readonly Dictionary<string, IDataSourceConnector> _connectors = new();

    public IReadOnlyList<string> SystemNames     { get; }
    public IReadOnlyList<string> DataSourceNames { get; }

    public LibraryRegistry(
        IOptions<RagRegistryOptions>          registryOpts,
        IOptions<VectorStoreOptions>          vsOpts,
        IVectorStoreFactory                   vectorStoreFactory,
        IOptions<LiteGraphOptions>            liteGraphOpts,
        IOptions<AzureOpenAIOptions>          aoaiOpts,
        IConnectorRegistry                    connectorRegistry,
        ITextExtractor                        extractor,
        ITextChunker                          chunker,
        IEmbeddingService                     embedder,
        ILogger<LibraryRegistry>              logger,
        IOptions<BatchingOptions>             batchingOpts,
        ILogger<JsonFileIndexStateStore>      stateLogger,
        ILogger<IndexingPipeline>             pipelineLogger)
    {
        _config = registryOpts.Value;
        _vsOpts = vsOpts.Value;

        logger.LogInformation(
            "Vector store backend: {Provider}", _vsOpts.Provider);

        // ── 1. Build one connector per data source ────────────────────────────
        foreach (var ds in _config.DataSources)
        {
            var connector = connectorRegistry.Resolve(ds);
            _connectors[ds.Name] = connector;
            logger.LogInformation(
                "Registered data source '{Name}' ({Type})", ds.Name, ds.Type);
        }

        // ── 2. Determine state store root directory ───────────────────────────
        // Use the LiteGraph database directory for state files regardless of
        // which vector store is active — state files are always JSON on disk.
        var stateDir = Path.GetDirectoryName(liteGraphOpts.Value.DatabasePath)
                       ?? AppContext.BaseDirectory;

        // ── 3. Build one vector store + state store + pipeline per system ──────
        foreach (var sys in _config.Systems)
        {
            foreach (var dsName in sys.DataSourceNames)
            {
                if (!_connectors.ContainsKey(dsName))
                    throw new InvalidOperationException(
                        $"RAG system '{sys.Name}' references unknown data source '{dsName}'. " +
                        $"Available: {string.Join(", ", _connectors.Keys)}");
            }

            // ── Vector store: delegate to the injected factory ─────────────────
            // LiteGraph → LiteGraphVectorStore (embedded SQLite, Tenant → Graph → Nodes)
            // Chroma    → ChromaVectorStore    (HTTP, one collection per system)
            var store = vectorStoreFactory.Create(sys);

            var state = new JsonFileIndexStateStore(sys.Name, stateDir, stateLogger);

            var sources = sys.DataSourceNames
                .Select(n => (_config.DataSources.First(d => d.Name == n), _connectors[n]))
                .ToList();

            var pipeline = new IndexingPipeline(
                sys, sources, extractor, chunker, embedder, store, state,
                batchingOpts.Value, pipelineLogger);

            _stores[sys.Name]    = store;
            _states[sys.Name]    = state;
            _pipelines[sys.Name] = pipeline;

            logger.LogInformation(
                "Registered RAG system '{Sys}' ← [{Sources}] [{Backend}]",
                sys.Name, string.Join(", ", sys.DataSourceNames), _vsOpts.Provider);
        }

        SystemNames     = [.. _config.Systems.Select(s => s.Name)];
        DataSourceNames = [.. _config.DataSources.Select(d => d.Name)];
    }

    public RagSystemDefinition   GetSystem(string name) =>
        _config.Systems.FirstOrDefault(s => s.Name == name)
        ?? throw new KeyNotFoundException($"RAG system '{name}' not found.");

    public DataSourceDefinition  GetDataSource(string name) =>
        _config.DataSources.FirstOrDefault(d => d.Name == name)
        ?? throw new KeyNotFoundException($"Data source '{name}' not found.");

    public IVectorStore          GetVectorStore(string systemName) =>
        _stores.TryGetValue(systemName, out var s) ? s
        : throw new KeyNotFoundException($"No vector store for system '{systemName}'.");

    public IIndexingPipeline     GetPipeline(string systemName) =>
        _pipelines.TryGetValue(systemName, out var p) ? p
        : throw new KeyNotFoundException($"No pipeline for system '{systemName}'.");

    public IIndexStateStore      GetStateStore(string systemName) =>
        _states.TryGetValue(systemName, out var s) ? s
        : throw new KeyNotFoundException($"No state store for system '{systemName}'.");

    public IDataSourceConnector  GetConnector(string dataSourceName) =>
        _connectors.TryGetValue(dataSourceName, out var c) ? c
        : throw new KeyNotFoundException($"No connector for data source '{dataSourceName}'.");

    public async Task<List<RagSystemStatus>> GetAllStatusAsync(CancellationToken ct = default)
    {
        var result = new List<RagSystemStatus>();

        foreach (var sys in _config.Systems)
        {
            var state = _states[sys.Name];
            bool healthy;
            try { healthy = await _stores[sys.Name].IndexExistsAsync(ct); }
            catch { healthy = false; }

            var dsStatuses = new List<DataSourceStatus>();
            foreach (var dsName in sys.DataSourceNames)
            {
                var ds        = GetDataSource(dsName);
                var connector = _connectors[dsName];
                var count     = await state.GetIndexedRecordCountAsync(dsName, ct);
                var last      = await state.GetLastFullIndexTimeAsync(dsName, ct);

                string connInfo;
                try   { connInfo = await connector.TestConnectionAsync(ct); }
                catch (Exception ex) { connInfo = $"Error: {ex.Message}"; }

                dsStatuses.Add(new DataSourceStatus(
                    DataSourceName:     dsName,
                    ConnectorType:      ds.Type.ToString(),
                    ConnectionInfo:     connInfo,
                    IsReachable:        !connInfo.StartsWith("Error:") &&
                                        !connInfo.StartsWith("Connection failed:"),
                    IndexedRecordCount: count,
                    LastFullIndex:      last,
                    LastDeltaIndex:     null,
                    ConnectionError:    connInfo.StartsWith("Error:") ||
                                        connInfo.StartsWith("Connection failed:")
                                        ? connInfo : null));
            }

            result.Add(new RagSystemStatus(sys.Name, sys.Description, healthy, dsStatuses));
        }

        return result;
    }
}
