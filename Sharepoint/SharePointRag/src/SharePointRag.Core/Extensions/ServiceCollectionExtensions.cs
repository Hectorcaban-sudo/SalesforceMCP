using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Services;

namespace SharePointRag.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all SharePoint RAG services.
    /// Call from any host (API, Worker, Console).
    /// </summary>
    public static IServiceCollection AddSharePointRag(
        this IServiceCollection services,
        IConfiguration configuration,
        string? stateFilePath = null)
    {
        // ── Options ───────────────────────────────────────────────────────────
        services.Configure<SharePointOptions>(configuration.GetSection(SharePointOptions.SectionName));
        services.Configure<AzureOpenAIOptions>(configuration.GetSection(AzureOpenAIOptions.SectionName));
        services.Configure<AzureSearchOptions>(configuration.GetSection(AzureSearchOptions.SectionName));
        services.Configure<ChunkingOptions>(configuration.GetSection(ChunkingOptions.SectionName));
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));

        // ── Microsoft Graph (SharePoint) ──────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SharePointOptions>>().Value;
            var credential = new ClientSecretCredential(
                opts.TenantId, opts.ClientId, opts.ClientSecret);
            return new GraphServiceClient(credential,
                ["https://graph.microsoft.com/.default"]);
        });

        // ── Azure OpenAI ──────────────────────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            return new AzureOpenAIClient(
                new Uri(opts.Endpoint),
                new AzureKeyCredential(opts.ApiKey));
        });

        // ── Azure AI Search ───────────────────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AzureSearchOptions>>().Value;
            return new SearchIndexClient(
                new Uri(opts.Endpoint),
                new AzureKeyCredential(opts.ApiKey));
        });

        // ── Text extractors ───────────────────────────────────────────────────
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxExtractor>();
        services.AddSingleton<ITextExtractor, PdfExtractor>();
        // The composite picks the right one at runtime:
        services.AddSingleton<CompositeTextExtractor>();
        // Re-register as ITextExtractor for IRagOrchestrator and IndexingPipeline:
        services.AddSingleton<ITextExtractor>(sp => sp.GetRequiredService<CompositeTextExtractor>());

        // ── Core services ─────────────────────────────────────────────────────
        services.AddSingleton<ITextChunker, TextChunker>();
        services.AddSingleton<IEmbeddingService, AzureOpenAIEmbeddingService>();
        services.AddSingleton<IVectorStore, AzureSearchVectorStore>();
        services.AddSingleton<ISharePointCrawler, SharePointCrawler>();
        services.AddSingleton<IRagOrchestrator, RagOrchestrator>();
        services.AddSingleton<IIndexingPipeline, IndexingPipeline>();

        // ── State store ───────────────────────────────────────────────────────
        var path = stateFilePath
            ?? Path.Combine(AppContext.BaseDirectory, "data", "index-state.json");
        services.AddSingleton<IIndexStateStore>(sp =>
            new JsonFileIndexStateStore(path,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonFileIndexStateStore>>()));

        return services;
    }
}
