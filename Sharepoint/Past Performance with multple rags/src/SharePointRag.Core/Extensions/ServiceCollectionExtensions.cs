using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Services;

namespace SharePointRag.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the full multi-library, multi-system RAG infrastructure.
    ///
    /// Configuration driven by appsettings.json "RagRegistry" section:
    ///   RagRegistry:
    ///     Libraries: [ { Name, SiteUrl, LibraryName, ... } ]
    ///     Systems:   [ { Name, LibraryNames: [...], TopK, MinScore } ]
    ///
    /// Agents then request their systems by name via ILibraryRegistry:
    ///   var store = registry.GetVectorStore("PastPerformance");
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

        // GlobalGraph is used as fallback credentials for libraries without their own app reg
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

        // ── Shared embedding service ──────────────────────────────────────────
        services.AddSingleton<IEmbeddingService, AzureOpenAIEmbeddingService>();
        services.AddSingleton<ITextChunker,      TextChunker>();

        // ── Library Registry (builds all crawlers, stores, pipelines) ─────────
        services.AddSingleton<ILibraryRegistry, LibraryRegistry>();

        // ── Multi-system RAG Orchestrator factory ─────────────────────────────
        // Consumers call CreateOrchestrator(["System1","System2"]) to get an
        // orchestrator that fans out across those systems.
        services.AddSingleton<IRagOrchestratorFactory, RagOrchestratorFactory>();

        return services;
    }
}

/// <summary>
/// Factory for creating RagOrchestrator instances bound to a specific set of system names.
/// Agents (and the PastPerformanceOrchestrator) use this to declare their systems.
/// </summary>
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
        // Validate all system names exist
        foreach (var name in systemNames)
            registry.GetSystem(name); // throws KeyNotFoundException if missing

        return new RagOrchestrator(
            systemNames, registry, embedder, openAi, aoaiOpts, agentOpts, logger);
    }
}
