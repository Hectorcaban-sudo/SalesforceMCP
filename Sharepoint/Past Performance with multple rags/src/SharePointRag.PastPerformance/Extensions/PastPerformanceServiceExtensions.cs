using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Services;

namespace SharePointRag.PastPerformance.Extensions;

public static class PastPerformanceServiceExtensions
{
    /// <summary>
    /// Registers all Past Performance Agent services.
    ///
    /// Call AFTER <c>services.AddSharePointRag()</c>.
    ///
    /// The agent's system names are configured in appsettings:
    ///   "PastPerformanceAgent": { "SystemNames": ["PastPerformance", "ProposalArchive"] }
    ///
    /// Those names must match keys in RagRegistry.Systems.
    /// </summary>
    public static IServiceCollection AddPastPerformanceAgent(
        this IServiceCollection services,
        IConfiguration configuration,
        RelevanceScorerOptions? scorerOptions = null)
    {
        // Bind per-agent options so the orchestrator knows which RAG systems to search
        services.Configure<PastPerformanceAgentOptions>(
            configuration.GetSection(PastPerformanceAgentOptions.SectionName));

        services.AddSingleton<IQueryParser,             LlmQueryParser>();
        services.AddSingleton<IContractExtractor,       LlmContractExtractor>();
        services.AddSingleton<IRelevanceScorer>(_  =>  new RelevanceScorer(scorerOptions));
        services.AddSingleton<IProposalDrafter,         ProposalDrafter>();
        services.AddSingleton<IPastPerformanceOrchestrator, PastPerformanceOrchestrator>();

        return services;
    }
}

/// <summary>
/// Configuration for the Past Performance Agent's RAG system membership.
/// Add to appsettings.json under "PastPerformanceAgent".
/// </summary>
public class PastPerformanceAgentOptions
{
    public const string SectionName = "PastPerformanceAgent";

    /// <summary>
    /// Names of the RAG systems this agent searches.
    /// Must match RagRegistry.Systems[*].Name in appsettings.
    ///
    /// Example:
    ///   ["PastPerformance"]                          → single library group
    ///   ["PastPerformance", "ProposalArchive"]       → fan-out across two groups
    /// </summary>
    public List<string> SystemNames { get; set; } = ["PastPerformance"];
}
