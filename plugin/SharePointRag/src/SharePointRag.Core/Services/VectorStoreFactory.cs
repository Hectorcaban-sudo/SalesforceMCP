using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Core.Services;

/// <summary>
/// Creates <see cref="LiteGraphVectorStore"/> instances.
/// Registered when <see cref="VectorStoreProvider.LiteGraph"/> is selected.
/// </summary>
public sealed class LiteGraphVectorStoreFactory(
    IOptions<LiteGraphOptions>            opts,
    IOptions<AzureOpenAIOptions>          aoaiOpts,
    ILogger<LiteGraphVectorStore>         logger) : IVectorStoreFactory
{
    public IVectorStore Create(RagSystemDefinition system) =>
        new LiteGraphVectorStore(system, opts.Value, aoaiOpts.Value, logger);
}

/// <summary>
/// Creates <see cref="ChromaVectorStore"/> instances.
/// Registered when <see cref="VectorStoreProvider.Chroma"/> is selected.
/// </summary>
public sealed class ChromaVectorStoreFactory(
    IOptions<ChromaOptions>        opts,
    ILogger<ChromaVectorStore>     logger) : IVectorStoreFactory
{
    public IVectorStore Create(RagSystemDefinition system) =>
        new ChromaVectorStore(system, opts.Value, logger);
}
