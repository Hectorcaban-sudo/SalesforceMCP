using Microsoft.Extensions.DependencyInjection;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Services;

namespace SharePointRag.PastPerformance.Extensions;

public static class PastPerformanceServiceExtensions
{
    /// <summary>
    /// Registers all Past Performance Agent services.
    /// Call AFTER <c>services.AddSharePointRag()</c> since this layer
    /// depends on <see cref="SharePointRag.Core"/> services being present.
    /// </summary>
    public static IServiceCollection AddPastPerformanceAgent(
        this IServiceCollection services,
        RelevanceScorerOptions? scorerOptions = null)
    {
        services.AddSingleton<IQueryParser,               LlmQueryParser>();
        services.AddSingleton<IContractExtractor,         LlmContractExtractor>();
        services.AddSingleton<IRelevanceScorer>(_ =>      new RelevanceScorer(scorerOptions));
        services.AddSingleton<IProposalDrafter,           ProposalDrafter>();
        services.AddSingleton<IPastPerformanceOrchestrator, PastPerformanceOrchestrator>();

        return services;
    }
}
