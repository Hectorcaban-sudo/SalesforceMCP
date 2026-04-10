using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using SharpCoreDB;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Services;

namespace SharePointRag.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all SharePoint RAG services.
    /// Call once from any host (API, Worker, Console).
    /// </summary>
    public static IServiceCollection AddSharePointRag(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Options ───────────────────────────────────────────────────────────
        services.Configure<SharePointOptions> (configuration.GetSection(SharePointOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(configuration.GetSection(AzureOpenAIOptions.SectionName));
        services.Configure<SharpCoreDbOptions>(configuration.GetSection(SharpCoreDbOptions.SectionName));
        services.Configure<ChunkingOptions>   (configuration.GetSection(ChunkingOptions.SectionName));
        services.Configure<AgentOptions>      (configuration.GetSection(AgentOptions.SectionName));

        // ── Microsoft Graph (SharePoint) ──────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var opts       = sp.GetRequiredService<IOptions<SharePointOptions>>().Value;
            var credential = new ClientSecretCredential(opts.TenantId, opts.ClientId, opts.ClientSecret);
            return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        });

        // ── Azure OpenAI ──────────────────────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            return new AzureOpenAIClient(new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
        });

        // ── SharpCoreDB – register AddSharpCoreDB() extension from the package
        services.AddSharpCoreDB();

        // ── Text extractors ───────────────────────────────────────────────────
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxExtractor>();
        services.AddSingleton<ITextExtractor, PdfExtractor>();
        services.AddSingleton<CompositeTextExtractor>();
        // Resolve the composite as the single ITextExtractor for pipeline consumers
        services.AddSingleton<ITextExtractor>(sp => sp.GetRequiredService<CompositeTextExtractor>());

        // ── Core services ─────────────────────────────────────────────────────
        services.AddSingleton<ITextChunker,        TextChunker>();
        services.AddSingleton<IEmbeddingService,   AzureOpenAIEmbeddingService>();
        services.AddSingleton<IVectorStore,        SharpCoreDbVectorStore>();
        services.AddSingleton<ISharePointCrawler,  SharePointCrawler>();
        services.AddSingleton<IRagOrchestrator,    RagOrchestrator>();
        services.AddSingleton<IIndexingPipeline,   IndexingPipeline>();

        // ── Index state store (delta tracking) ────────────────────────────────
        // Stored in the same SharpCoreDB data directory so it's also encrypted.
        services.AddSingleton<IIndexStateStore>(sp =>
        {
            var scdbOpts = sp.GetRequiredService<IOptions<SharpCoreDbOptions>>().Value;
            var statePath = Path.Combine(scdbOpts.DataDirectory, "index-state.json");
            return new JsonFileIndexStateStore(
                statePath,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonFileIndexStateStore>>());
        });

        return services;
    }
}
