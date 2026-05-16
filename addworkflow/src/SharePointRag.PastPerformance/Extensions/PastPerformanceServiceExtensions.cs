using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Plugins;
using SharePointRag.PastPerformance.Services;

namespace SharePointRag.PastPerformance.Extensions;

public static class PastPerformanceServiceExtensions
{
    /// <summary>
    /// Registers all Past Performance Agent services including the external AI plugin system.
    ///
    /// Call AFTER <c>services.AddSharePointRag()</c>.
    ///
    /// The agent's RAG systems are configured in appsettings:
    ///   "PastPerformanceAgent": { "SystemNames": ["PastPerformance", "Contracts"] }
    ///
    /// External AI plugins are configured in appsettings:
    ///   "PastPerformanceAgent": {
    ///     "ExternalPlugins": [
    ///       { "Name": "sam_gov_ai", "EndpointType": "OpenAiCompatible", ... },
    ///       { "Name": "deltek_ai", "EndpointType": "CustomHttp", ... }
    ///     ]
    ///   }
    ///
    /// Custom plugins can also be registered programmatically:
    ///   services.AddExternalAiPlugin&lt;MyPlugin&gt;();
    /// </summary>
    public static IServiceCollection AddPastPerformanceAgent(
        this IServiceCollection services,
        IConfiguration configuration,
        RelevanceScorerOptions? scorerOptions = null)
    {
        services.Configure<PastPerformanceAgentOptions>(
            configuration.GetSection(PastPerformanceAgentOptions.SectionName));

        // ── Core PP services ──────────────────────────────────────────────────
        services.AddSingleton<IQueryParser,         LlmQueryParser>();
        services.AddSingleton<IContractExtractor,   LlmContractExtractor>();
        services.AddSingleton<IRelevanceScorer>(_ => new RelevanceScorer(scorerOptions));
        services.AddSingleton<IProposalDrafter,     ProposalDrafter>();

        // ── Plugin system ─────────────────────────────────────────────────────
        // PluginRouter is built as a singleton that collects:
        //   a) Built-in plugins from PastPerformanceAgent.ExternalPlugins[] config
        //   b) Any IExternalAiPlugin registered via services.AddExternalAiPlugin<T>()
        services.AddSingleton<IPluginRouter>(sp =>
        {
            var agentOpts = sp.GetRequiredService<IOptions<PastPerformanceAgentOptions>>().Value;
            var openAi    = sp.GetRequiredService<AzureOpenAIClient>();
            var aoai      = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
            var logger    = sp.GetRequiredService<ILogger<PluginRouter>>();

            // Collect all plugin definitions from config
            var defs = agentOpts.ExternalPlugins.Where(p => p.Enabled).ToList();

            // Build built-in plugin instances from config
            var builtInPlugins = new List<IExternalAiPlugin>();
            foreach (var def in defs)
            {
                var pluginLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(def.Name);
                IExternalAiPlugin plugin = def.EndpointType switch
                {
                    ExternalAiEndpointType.OpenAiCompatible => new OpenAiCompatiblePlugin(def, pluginLogger),
                    ExternalAiEndpointType.CustomHttp       => new CustomHttpPlugin(def, pluginLogger),
                    ExternalAiEndpointType.MicrosoftCopilot => new MicrosoftCopilotPlugin(def, pluginLogger),
                    _ => throw new InvalidOperationException(
                        $"Unknown EndpointType '{def.EndpointType}' for plugin '{def.Name}'.")
                };
                builtInPlugins.Add(plugin);
            }

            // Collect any programmatically registered IExternalAiPlugin instances
            var customPlugins = sp.GetServices<IExternalAiPlugin>().ToList();

            var allPlugins = builtInPlugins.Concat(customPlugins).ToList();

            logger.LogInformation(
                "[PP] Plugin router: {B} config plugins + {C} custom plugins = {T} total",
                builtInPlugins.Count, customPlugins.Count, allPlugins.Count);

            return new PluginRouter(allPlugins, defs, openAi, aoai, logger);
        });

        // ── Main orchestrator ─────────────────────────────────────────────────
        services.AddSingleton<IPastPerformanceOrchestrator, PastPerformanceOrchestrator>();

        return services;
    }

    /// <summary>
    /// Registers services required by the <c>Microsoft.Agents.AI.Workflows</c>-based
    /// Past Performance Workflow alongside the existing stateless PastPerformanceAgent.
    ///
    /// What this registers:
    ///   - <see cref="PPOrchestratorChatClient"/> — adapts IPastPerformanceOrchestrator
    ///     to IChatClient so it can be a workflow node via chatClient.AsAIAgent()
    ///   - IChatClient (if not already registered) — Azure OpenAI client for the
    ///     QueryParserAgent and ResponseFormatterAgent steps
    ///
    /// The workflow itself is built via builder.AddWorkflow() in Program.cs using
    /// <see cref="PastPerformanceWorkflowFactory.Build"/> and then exposed as an
    /// AIAgent via .AddAsAIAgent().
    ///
    /// Endpoint: POST /api/pastperformance/workflow/run  (PPWorkflowController)
    ///           POST /api/pastperformance/workflow/stream (streaming SSE)
    ///
    /// All PP domain services (orchestrator, extractor, scorer, drafter) are already
    /// registered by AddPastPerformanceAgent() — no duplication here.
    /// </summary>
    public static IServiceCollection AddPastPerformanceWorkflow(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // IChatClient — Azure OpenAI as IChatClient for all ChatClientAgent nodes.
        // Used by RouterAgent + all sub-workflow agents (ContractFinder, NarrativeDrafter, etc.)
        // This uses Azure.AI.OpenAI's AsIChatClient() extension which makes the
        // AzureOpenAIClient's ChatClient conform to Microsoft.Extensions.AI.IChatClient.
        // Guard: only register if not already registered by another component.
        services.TryAddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                SharePointRag.Core.Configuration.AzureOpenAIOptions>>().Value;
            return new Azure.AI.OpenAI.AzureOpenAIClient(
                    new Uri(opts.Endpoint),
                    new Azure.AzureKeyCredential(opts.ApiKey))
                .GetChatClient(opts.ChatDeployment)
                .AsIChatClient();
        });

        return services;
    }

    /// <summary>
    /// Register a custom external AI plugin implementation.
    /// The plugin will be picked up automatically by the PluginRouter.
    ///
    /// Usage:
    ///   services.AddExternalAiPlugin&lt;MyCrmAiPlugin&gt;();
    /// </summary>
    public static IServiceCollection AddExternalAiPlugin<TPlugin>(
        this IServiceCollection services)
        where TPlugin : class, IExternalAiPlugin
    {
        services.AddSingleton<TPlugin>();
        services.AddSingleton<IExternalAiPlugin>(sp => sp.GetRequiredService<TPlugin>());
        return services;
    }
}

// ── Options ───────────────────────────────────────────────────────────────────

public class PastPerformanceAgentOptions
{
    public const string SectionName = "PastPerformanceAgent";

    /// <summary>RAG system names to search. Must match RagRegistry.Systems[*].Name.</summary>
    public List<string> SystemNames { get; set; } = ["PastPerformance"];

    /// <summary>
    /// External AI plugins available to the Past Performance Agent.
    /// Each entry creates one IExternalAiPlugin at startup.
    /// Plugins with Enabled=false are skipped.
    ///
    /// Example:
    ///   "ExternalPlugins": [
    ///     {
    ///       "Name":         "sam_gov_ai",
    ///       "Description":  "Queries SAM.gov for federal contract awards and vendor data.",
    ///       "EndpointType": "OpenAiCompatible",
    ///       "Endpoint":     "https://sam-gov-ai.example.com/v1",
    ///       "ApiKey":       "{SAM_GOV_API_KEY}",
    ///       "Model":        "gpt-4o",
    ///       "Enabled":      true,
    ///       "AlwaysInvokeForIntents": ["IdentifyGaps"]
    ///     }
    ///   ]
    /// </summary>
    public List<ExternalAiPluginDefinition> ExternalPlugins { get; set; } = [];
}
