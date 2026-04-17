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
    /// Configuration driven by "RagRegistry" in appsettings.json:
    ///   RagRegistry:
    ///     DataSources:
    ///       - { Name, Type, Properties: { ... } }
    ///     Systems:
    ///       - { Name, DataSourceNames: [...], TopK, MinScore }
    ///
    /// Built-in connectors registered automatically:
    ///   SharePoint  → SharePointConnectorFactory
    ///   SqlDatabase → SqlConnectorFactory
    ///   Excel       → ExcelConnectorFactory
    ///   Deltek      → DeltekConnectorFactory
    ///   Custom      → CustomConnectorFactory  (resolved via "CustomType" property)
    ///
    /// Add your own connector:
    ///   services.AddCustomConnector&lt;MyCrmConnector, MyCrmConnectorFactory&gt;();
    /// </summary>
    public static IServiceCollection AddSharePointRag(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Options ───────────────────────────────────────────────────────────
        services.Configure<RagRegistryOptions>(configuration.GetSection(RagRegistryOptions.SectionName));
        services.Configure<SharpCoreDbOptions>(configuration.GetSection(SharpCoreDbOptions.SectionName));
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
            return new AzureOpenAIClient(new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
        });

        // ── SharpCoreDB ───────────────────────────────────────────────────────
        services.AddSharpCoreDB();

        // ── Text extractors ───────────────────────────────────────────────────
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxExtractor>();
        services.AddSingleton<ITextExtractor, PdfExtractor>();
        services.AddSingleton<CompositeTextExtractor>();
        services.AddSingleton<ITextExtractor>(sp => sp.GetRequiredService<CompositeTextExtractor>());

        // ── Embedding + chunking (shared) ─────────────────────────────────────
        services.AddSingleton<IEmbeddingService, AzureOpenAIEmbeddingService>();
        services.AddSingleton<ITextChunker,      TextChunker>();

        // ── Connector registry + built-in factories ───────────────────────────
        services.AddSingleton<IConnectorRegistry>(sp =>
        {
            var registry = new ConnectorRegistry();

            // SharePoint
            registry.Register(new SharePointConnectorFactory(
                sp.GetRequiredService<GlobalGraphOptions>(),
                sp.GetRequiredService<ITextExtractor>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharePointConnector>>()));

            // SQL (SQL Server / Postgres / MySQL / SQLite)
            registry.Register(new SqlConnectorFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlConnector>>()));

            // Excel / CSV
            registry.Register(new ExcelConnectorFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExcelConnector>>()));

            // Deltek Vantagepoint
            registry.Register(new DeltekConnectorFactory(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeltekConnector>>()));

            // Custom (resolves via "CustomType" property using reflection + DI)
            registry.Register(new CustomConnectorFactory(
                sp,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CustomConnectorFactory>>()));

            // Any additional IDataSourceConnectorFactory registrations added via
            // services.AddCustomConnector<T,F>() are picked up automatically
            // because they are registered as IDataSourceConnectorFactory in DI.
            foreach (var extraFactory in sp.GetServices<IDataSourceConnectorFactory>())
                registry.Register(extraFactory);

            return registry;
        });

        // ── Library Registry (builds all connectors, stores, pipelines) ────────
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
            registry.GetSystem(name); // validates existence, throws if missing

        return new RagOrchestrator(systemNames, registry, embedder, openAi, aoaiOpts, agentOpts, logger);
    }
}
