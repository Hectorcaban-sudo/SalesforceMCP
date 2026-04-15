using SharePointRag.Core.Models;
using SharePointRag.PastPerformance.Models;

namespace SharePointRag.PastPerformance.Interfaces;

/// <summary>
/// Parses a natural-language question into a structured PastPerformanceQuery,
/// extracting intent, filters, and semantic search text via LLM.
/// </summary>
public interface IQueryParser
{
    Task<PastPerformanceQuery> ParseAsync(string rawQuestion, CancellationToken ct = default);
}

/// <summary>
/// Uses the structured query + retrieved chunks to extract structured
/// ContractRecord objects from unstructured source documents.
/// </summary>
public interface IContractExtractor
{
    /// <summary>Extract one or more ContractRecords from a set of retrieved document chunks.</summary>
    Task<List<ContractRecord>> ExtractAsync(
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default);
}

/// <summary>
/// Scores and ranks ContractRecords by relevance to a query, applying
/// GovCon-specific rules (recency, NAICS match, value range, CPARS ratings).
/// </summary>
public interface IRelevanceScorer
{
    List<ContractRecord> ScoreAndRank(
        List<ContractRecord> contracts,
        PastPerformanceQuery query);
}

/// <summary>
/// Drafts a proposal-ready Past Performance Volume section from ranked contracts.
/// Follows common RFP instructions (FAR 15.305, agency-specific formats).
/// </summary>
public interface IProposalDrafter
{
    Task<PastPerformanceVolumeSection> DraftVolumeAsync(
        List<ContractRecord> contracts,
        string solicitationContext,
        CancellationToken ct = default);

    Task<ContractNarrative> DraftNarrativeAsync(
        ContractRecord contract,
        string solicitationContext,
        CancellationToken ct = default);
}

/// <summary>
/// Top-level orchestrator for the Past Performance Agent.
/// Composes query parsing → vector search → extraction → scoring → drafting.
/// </summary>
public interface IPastPerformanceOrchestrator
{
    Task<PastPerformanceResponse> HandleAsync(
        string userMessage,
        CancellationToken ct = default);
}
