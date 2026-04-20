using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Connectors;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Services;

namespace SharePointRag.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full multi-source, multi-system RAG infrastructure.
    ///
    /// Vector + graph storage: LiteGraph (embedded SQLite, no server required).
    ///   • One SQLite file at LiteGraph:DatabasePath
    ///   • One Tenant per deployment, one Graph per RAG system
    ///   • Nodes store chunk content + metadata in Data; embeddings in Vectors
    ///   • Tags (SourceId, DataSourceName) enable efficient delta deletes
    ///
    /// Built-in connectors:
    ///   SharePoint, SqlDatabase, Excel, Deltek, Custom
    ///
    /// Add your own:
    ///   services.AddCustomConnector&lt;MyConnector, MyFactory&gt;();
    /// </summary>
    public static IServiceCollection AddSharePointRag(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Options ───────────────────────────────────────────────────────────
        services.Configure<RagRegistryOptions>(configuration.GetSection(RagRegistryOptions.SectionName));
        services.Configure<LiteGraphOptions>  (configuration.GetSection(LiteGraphOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(configuration.GetSection(AzureOpenAIOptions.SectionName));
        services.Configure<ChunkingOptions>   (configuration.GetSection(ChunkingOptions.SectionName));
        services.Configure<AgentOptions>      (configuration.GetSection(AgentOptions.SectionName));

        // Global Graph credentials — fallback for SharePoint connectors
        services.AddSingleton(sp =>
        {
            var cfg = configuration.GetSection(GlobalGraphOptions.SectionName);
            return new GlobalGraphOptions
            {
                TenantId     = cfg["TenantId"]     ?? string.Empty,
                ClientId     = cfg["ClientId"]     ?? string.Empty,
                ClientSecret = cfg["ClientSecret"] ?? string.Empty
            };
        });

        // ── Azure OpenAI (shared across all systems) ──────────────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            return new AzureOpenAIClient(
                new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
        });

        // ── Text extractors ───────────────────────────────────────────────────
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxExtractor>();
        services.AddSingleton<ITextExtractor, PdfExtractor>();
        services.AddSingleton<CompositeTextExtractor>();
        services.AddSingleton<ITextExtractor>(
            sp => sp.GetRequiredService<CompositeTextExtractor>());

        // ── Embedding + chunking ──────────────────────────────────────────────
        services.AddSingleton<IEmbeddingService, AzureOpenAIEmbeddingService>();
        services.AddSingleton<ITextChunker,      TextChunker>();

        // ── Connector registry with all built-in factories ────────────────────
        services.AddSingleton<IConnectorRegistry>(sp =>
        {
            var registry = new ConnectorRegistry();

            registry.Register(new SharePointConnectorFactory(
                sp.GetRequiredService<GlobalGraphOptions>(),
                sp.GetRequiredService<ITextExtractor>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharePointConnector>>()));

            registry.Register(new SqlConnectorFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlConnector>>()));

            registry.Register(new ExcelConnectorFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExcelConnector>>()));

            registry.Register(new DeltekConnectorFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeltekConnector>>()));

            registry.Register(new CustomConnectorFactory(
                sp,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CustomConnectorFactory>>()));

            // Pick up any IDataSourceConnectorFactory instances registered via
            // services.AddCustomConnector<T,F>()
            foreach (var extra in sp.GetServices<IDataSourceConnectorFactory>())
                registry.Register(extra);

            return registry;
        });

        // ── Library Registry (builds LiteGraph stores + pipelines from config) ─
        services.AddSingleton<ILibraryRegistry, LibraryRegistry>();

        // ── Orchestrator factory ──────────────────────────────────────────────
        services.AddSingleton<IRagOrchestratorFactory, RagOrchestratorFactory>();

        return services;
    }
}

// ── Orchestrator factory ──────────────────────────────────────────────────────

public interface IRagOrchestratorFactory
{
    IRagOrchestrator Create(IReadOnlyList<string> systemNames);
}

public sealed class RagOrchestratorFactory(
    ILibraryRegistry registry,
    IEmbeddingService embedder,
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    IOptions<AgentOptions> agentOpts,
    Microsoft.Extensions.Logging.ILogger<RagOrchestrator> logger) : IRagOrchestratorFactory
{
    public IRagOrchestrator Create(IReadOnlyList<string> systemNames)
    {
        foreach (var name in systemNames)
            registry.GetSystem(name); // validates existence
        return new RagOrchestrator(
            systemNames, registry, embedder, openAi, aoaiOpts, agentOpts, logger);
    }
}
