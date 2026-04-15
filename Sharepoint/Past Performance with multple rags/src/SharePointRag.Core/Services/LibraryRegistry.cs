using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Central runtime registry of all configured RAG systems.
///
/// At startup it materialises one set of services per system:
///   • One IVectorStore          per system  (isolated SharpCoreDB directory)
///   • One IIndexStateStore      per system
///   • One IIndexingPipeline     per system  (fans across its assigned libraries)
///   • One ISharePointCrawler    per library (shared if the same library feeds multiple systems)
///
/// Agents call GetVectorStore("PastPerformance") and get back an already-initialised,
/// named instance without knowing anything about the others.
/// </summary>
public sealed class LibraryRegistry : ILibraryRegistry
{
    private readonly RagRegistryOptions _config;
    private readonly Dictionary<string, IVectorStore>      _stores     = new();
    private readonly Dictionary<string, IIndexingPipeline> _pipelines  = new();
    private readonly Dictionary<string, IIndexStateStore>  _states     = new();
    private readonly Dictionary<string, ISharePointCrawler> _crawlers   = new();

    public IReadOnlyList<string> SystemNames  { get; }
    public IReadOnlyList<string> LibraryNames { get; }

    public LibraryRegistry(
        IOptions<RagRegistryOptions>       registryOpts,
        IOptions<SharpCoreDbOptions>       scdbOpts,
        IOptions<AzureOpenAIOptions>       aoaiOpts,
        GlobalGraphOptions                 globalGraph,
        ITextExtractor                     extractor,
        ITextChunker                       chunker,
        IEmbeddingService                  embedder,
        ILogger<LibraryRegistry>           logger,
        ILogger<SharpCoreDbVectorStore>    storeLogger,
        ILogger<JsonFileIndexStateStore>   stateLogger,
        ILogger<IndexingPipeline>          pipelineLogger,
        ILogger<SharePointCrawler>         crawlerLogger)
    {
        _config = registryOpts.Value;

        // ── 1. Build one crawler per library (reused if library appears in multiple systems)
        foreach (var lib in _config.Libraries)
        {
            _crawlers[lib.Name] = new SharePointCrawler(lib, globalGraph, crawlerLogger);
            logger.LogInformation("Registered library '{Lib}' → {Site}/{Doc}",
                lib.Name, lib.SiteUrl, lib.LibraryName);
        }

        // ── 2. Build one vector store + state store + pipeline per system
        foreach (var sys in _config.Systems)
        {
            // Validate that all referenced libraries exist
            foreach (var libName in sys.LibraryNames)
            {
                if (!_crawlers.ContainsKey(libName))
                    throw new InvalidOperationException(
                        $"RAG system '{sys.Name}' references unknown library '{libName}'. " +
                        $"Available libraries: {string.Join(", ", _crawlers.Keys)}");
            }

            var store = new SharpCoreDbVectorStore(
                sys, scdbOpts.Value, aoaiOpts.Value, storeLogger);

            var state = new JsonFileIndexStateStore(
                sys.Name, scdbOpts.Value.DataDirectory, stateLogger);

            var libCrawlers = sys.LibraryNames.Select(n => _crawlers[n]).ToList();

            var pipeline = new IndexingPipeline(
                sys, libCrawlers, extractor, chunker, embedder, store, state, pipelineLogger);

            _stores[sys.Name]    = store;
            _states[sys.Name]    = state;
            _pipelines[sys.Name] = pipeline;

            logger.LogInformation(
                "Registered RAG system '{Sys}' ← libraries [{Libs}]",
                sys.Name, string.Join(", ", sys.LibraryNames));
        }

        SystemNames  = [.. _config.Systems.Select(s => s.Name)];
        LibraryNames = [.. _config.Libraries.Select(l => l.Name)];
    }

    public RagSystemDefinition GetSystem(string name) =>
        _config.Systems.FirstOrDefault(s => s.Name == name)
        ?? throw new KeyNotFoundException($"RAG system '{name}' not found.");

    public LibraryDefinition GetLibrary(string name) =>
        _config.Libraries.FirstOrDefault(l => l.Name == name)
        ?? throw new KeyNotFoundException($"Library '{name}' not found.");

    public IVectorStore      GetVectorStore(string systemName) =>
        _stores.TryGetValue(systemName, out var s) ? s
        : throw new KeyNotFoundException($"No vector store for system '{systemName}'.");

    public IIndexingPipeline GetPipeline(string systemName) =>
        _pipelines.TryGetValue(systemName, out var p) ? p
        : throw new KeyNotFoundException($"No pipeline for system '{systemName}'.");

    public IIndexStateStore  GetStateStore(string systemName) =>
        _states.TryGetValue(systemName, out var s) ? s
        : throw new KeyNotFoundException($"No state store for system '{systemName}'.");

    public ISharePointCrawler GetCrawler(string libraryName) =>
        _crawlers.TryGetValue(libraryName, out var c) ? c
        : throw new KeyNotFoundException($"No crawler for library '{libraryName}'.");

    public async Task<List<RagSystemStatus>> GetAllStatusAsync(CancellationToken ct = default)
    {
        var result = new List<RagSystemStatus>();

        foreach (var sys in _config.Systems)
        {
            var store  = _stores[sys.Name];
            var state  = _states[sys.Name];
            bool exists;
            try { exists = await store.IndexExistsAsync(ct); }
            catch { exists = false; }

            var libStatuses = new List<LibraryStatus>();
            foreach (var libName in sys.LibraryNames)
            {
                var lib   = GetLibrary(libName);
                var count = await state.GetIndexedFileCountAsync(libName, ct);
                var last  = await state.GetLastFullIndexTimeAsync(libName, ct);
                libStatuses.Add(new LibraryStatus(
                    LibraryName:     libName,
                    SiteUrl:         lib.SiteUrl,
                    DocumentLibrary: lib.LibraryName,
                    IndexExists:     exists,
                    IndexedFileCount: count,
                    LastFullIndex:   last,
                    LastDeltaIndex:  null));
            }

            result.Add(new RagSystemStatus(
                SystemName:  sys.Name,
                Description: sys.Description,
                IsHealthy:   exists,
                Libraries:   libStatuses));
        }

        return result;
    }
}
