using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Core.Services;

/// <summary>
/// Creates <see cref="LiteGraphVectorStore"/> instances scoped to one (system, data source) pair.
/// The graph name is "{systemName}__{dataSourceName}" — globally unique within the SQLite file.
/// </summary>
public sealed class LiteGraphVectorStoreFactory(
    IOptions<LiteGraphOptions>    opts,
    IOptions<AzureOpenAIOptions>  aoaiOpts,
    ILogger<LiteGraphVectorStore> logger) : IVectorStoreFactory
{
    public IVectorStore Create(RagSystemDefinition system, DataSourceDefinition dataSource) =>
        new LiteGraphVectorStore(
            system.Name, dataSource.Name,
            opts.Value, aoaiOpts.Value, logger);
}

/// <summary>
/// Creates <see cref="ChromaVectorStore"/> instances scoped to one (system, data source) pair.
/// The collection name is "{CollectionPrefix}{systemName}__{dataSourceName}".
/// </summary>
public sealed class ChromaVectorStoreFactory(
    IOptions<ChromaOptions>      opts,
    ILogger<ChromaVectorStore>   logger) : IVectorStoreFactory
{
    public IVectorStore Create(RagSystemDefinition system, DataSourceDefinition dataSource) =>
        new ChromaVectorStore(system, dataSource.Name, opts.Value, logger);
}
