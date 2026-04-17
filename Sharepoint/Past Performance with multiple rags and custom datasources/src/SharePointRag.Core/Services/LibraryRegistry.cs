using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Connectors;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Central runtime registry — source-agnostic.
///
/// Builds one set of services per RAG system at startup:
///   IDataSourceConnector per data source (SharePoint, SQL, Excel, Deltek, Custom…)
///   IVectorStore         per system (isolated SharpCoreDB sub-directory)
///   IIndexStateStore     per system
///   IIndexingPipeline    per system (fans over all assigned connectors)
///
/// New connector types need zero changes here — the ConnectorRegistry factory
/// dispatches by DataSourceType automatically.
/// </summary>
public sealed class LibraryRegistry : ILibraryRegistry
{
    private readonly RagRegistryOptions _config;
    private readonly Dictionary<string, IVectorStore>         _stores     = new();
    private readonly Dictionary<string, IIndexingPipeline>    _pipelines  = new();
    private readonly Dictionary<string, IIndexStateStore>     _states     = new();
    private readonly Dictionary<string, IDataSourceConnector> _connectors = new();

    public IReadOnlyList<string> SystemNames     { get; }
    public IReadOnlyList<string> DataSourceNames { get; }

    public LibraryRegistry(
        IOptions<RagRegistryOptions>       registryOpts,
        IOptions<SharpCoreDbOptions>       scdbOpts,
        IOptions<AzureOpenAIOptions>       aoaiOpts,
        IConnectorRegistry                 connectorRegistry,
        ITextExtractor                     extractor,
        ITextChunker                       chunker,
        IEmbeddingService                  embedder,
        ILogger<LibraryRegistry>           logger,
        ILogger<SharpCoreDbVectorStore>    storeLogger,
        ILogger<JsonFileIndexStateStore>   stateLogger,
        ILogger<IndexingPipeline>          pipelineLogger)
    {
        _config = registryOpts.Value;

        // ── 1. Build one connector per data source ────────────────────────────
        foreach (var ds in _config.DataSources)
        {
            var connector = connectorRegistry.Resolve(ds);
            _connectors[ds.Name] = connector;
            logger.LogInformation(
                "Registered data source '{Name}' ({Type})",
                ds.Name, ds.Type);
        }

        // ── 2. Build one store + state + pipeline per RAG system ──────────────
        foreach (var sys in _config.Systems)
        {
            foreach (var dsName in sys.DataSourceNames)
            {
                if (!_connectors.ContainsKey(dsName))
                    throw new InvalidOperationException(
                        $"RAG system '{sys.Name}' references unknown data source '{dsName}'. " +
                        $"Available: {string.Join(", ", _connectors.Keys)}");
            }

            var store = new SharpCoreDbVectorStore(
                sys, scdbOpts.Value, aoaiOpts.Value, storeLogger);

            var state = new JsonFileIndexStateStore(
                sys.Name, scdbOpts.Value.DataDirectory, stateLogger);

            var sources = sys.DataSourceNames.Select(n =>
                (_config.DataSources.First(d => d.Name == n), _connectors[n])).ToList();

            var pipeline = new IndexingPipeline(
                sys, sources, extractor, chunker, embedder, store, state, pipelineLogger);

            _stores[sys.Name]    = store;
            _states[sys.Name]    = state;
            _pipelines[sys.Name] = pipeline;

            logger.LogInformation(
                "Registered RAG system '{Sys}' ← [{Sources}]",
                sys.Name, string.Join(", ", sys.DataSourceNames));
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
                    DataSourceName:  dsName,
                    ConnectorType:   ds.Type.ToString(),
                    ConnectionInfo:  connInfo,
                    IsReachable:     !connInfo.StartsWith("Error:") && !connInfo.StartsWith("Connection failed:"),
                    IndexedRecordCount: count,
                    LastFullIndex:   last,
                    LastDeltaIndex:  null,
                    ConnectionError: connInfo.StartsWith("Error:") || connInfo.StartsWith("Connection failed:")
                                     ? connInfo : null));
            }

            result.Add(new RagSystemStatus(sys.Name, sys.Description, healthy, dsStatuses));
        }

        return result;
    }
}
