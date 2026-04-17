using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Extensions;
using SharePointRag.Core.Interfaces;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Prompts;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Top-level orchestrator for the Past Performance Agent.
///
/// Multi-library aware: the agent declares its RAG system names via
/// <see cref="PastPerformanceAgentOptions.SystemNames"/>. The orchestrator
/// fans out vector search across ALL assigned systems (e.g. "PastPerformanceDocs"
/// + "ProposalArchive") and merges results before extraction.
///
/// Pipeline per request:
///   1. Parse intent              → IQueryParser (GPT-4o structured extraction)
///   2. Embed semantic query      → IEmbeddingService
///   3. Fan-out KNN search        → IRagOrchestrator (covers all assigned systems)
///   4. Extract ContractRecords   → IContractExtractor (GPT-4o per source file)
///   5. Score and rank            → IRelevanceScorer (GovCon-specific weights)
///   6. Route by intent:
///        GenerateVolumeSection   → IProposalDrafter full volume
///        FindReferences          → CO/COR contact blocks
///        ExtractCPARSRatings     → ratings markdown table
///        FindKeyPersonnel        → personnel roster
///        SummarisePortfolio      → GPT-4o portfolio summary
///        IdentifyGaps            → GPT-4o gap analysis
///        FindSimilarContracts    → ranked list + grounded answer
///        General                 → grounded answer
/// </summary>
public sealed class PastPerformanceOrchestrator : IPastPerformanceOrchestrator
{
    private readonly IQueryParser         _queryParser;
    private readonly IRagOrchestrator     _ragOrchestrator;   // multi-system fan-out
    private readonly IContractExtractor   _extractor;
    private readonly IRelevanceScorer     _scorer;
    private readonly IProposalDrafter     _drafter;
    private readonly AzureOpenAIClient    _openAi;
    private readonly AzureOpenAIOptions   _aoai;
    private readonly ILogger<PastPerformanceOrchestrator> _logger;

    public PastPerformanceOrchestrator(
        IQueryParser queryParser,
        IRagOrchestratorFactory orchestratorFactory,
        IOptions<PastPerformanceAgentOptions> agentOpts,
        IContractExtractor extractor,
        IRelevanceScorer scorer,
        IProposalDrafter drafter,
        AzureOpenAIClient openAi,
        IOptions<AzureOpenAIOptions> aoaiOpts,
        ILogger<PastPerformanceOrchestrator> logger)
    {
        _queryParser = queryParser;
        _extractor   = extractor;
        _scorer      = scorer;
        _drafter     = drafter;
        _openAi      = openAi;
        _aoai        = aoaiOpts.Value;
        _logger      = logger;

        // Build a multi-system orchestrator covering all PP-assigned RAG systems.
        // e.g. ["PastPerformanceDocs", "ProposalArchive"] → fan-out across both.
        var systemNames = agentOpts.Value.SystemNames.Count > 0
            ? agentOpts.Value.SystemNames
            : (IReadOnlyList<string>)["PastPerformance"];

        _ragOrchestrator = orchestratorFactory.Create(systemNames);

        _logger.LogInformation(
            "PastPerformance orchestrator covers RAG systems: [{S}]",
            string.Join(", ", systemNames));
    }

    public async Task<PastPerformanceResponse> HandleAsync(
        string userMessage, CancellationToken ct = default)
    {
        _logger.LogInformation("PastPerformance request: {M}", userMessage);

        // ── 1. Parse intent ───────────────────────────────────────────────────
        var query = await _queryParser.ParseAsync(userMessage, ct);
        _logger.LogDebug("Intent={I}, SemanticQuery={Q}", query.Intent, query.SemanticQuery);

        // ── 2 & 3. Embed + multi-system vector search ─────────────────────────
        // IRagOrchestrator already handles embed + fan-out + merge internally.
        // We pass the semantic query as the "question" and grab the Sources.
        var ragResponse = await _ragOrchestrator.AskAsync(query.SemanticQuery, ct);
        var chunks      = ragResponse.Sources;

        _logger.LogInformation(
            "Retrieved {N} chunks across {S} system(s)",
            chunks.Count, _ragOrchestrator.SystemNames.Count);

        if (chunks.Count == 0)
        {
            return new PastPerformanceResponse
            {
                Query    = query,
                Answer   = "I could not find any relevant past performance records in the " +
                           "document library for your query. Please ensure your contracts and " +
                           "performance reports have been indexed.",
                Warnings = ["No matching documents found in the vector index."]
            };
        }

        // ── 4. Extract structured ContractRecords ─────────────────────────────
        var contracts = await _extractor.ExtractAsync(chunks, ct);

        // ── 5. Score and rank ─────────────────────────────────────────────────
        var ranked = _scorer.ScoreAndRank(contracts, query);

        // ── 6. Route by intent ────────────────────────────────────────────────
        return query.Intent switch
        {
            QueryIntent.GenerateVolumeSection =>
                await HandleVolumeDraftAsync(query, ranked, userMessage, ct),

            QueryIntent.SummarisePortfolio =>
                await HandlePortfolioSummaryAsync(query, ranked, ct),

            QueryIntent.IdentifyGaps =>
                await HandleGapAnalysisAsync(query, ranked, userMessage, ct),

            QueryIntent.FindReferences =>
                HandleFindReferences(query, ranked),

            QueryIntent.ExtractCPARSRatings =>
                HandleExtractCpars(query, ranked),

            QueryIntent.FindKeyPersonnel =>
                HandleFindKeyPersonnel(query, ranked),

            _ => await HandleGeneralAsync(query, ranked, ct)
        };
    }

    // ── Intent handlers (unchanged logic, parameter tweaks only) ─────────────

    private async Task<PastPerformanceResponse> HandleVolumeDraftAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked,
        string solicitationContext, CancellationToken ct)
    {
        var topContracts = ranked.Take(5).ToList();
        var volume = await _drafter.DraftVolumeAsync(topContracts, solicitationContext, ct);

        var sb = new StringBuilder();
        sb.AppendLine("## 📋 Past Performance Volume Draft");
        sb.AppendLine();
        sb.AppendLine($"**Executive Summary:** {volume.ExecutiveSummary}");
        sb.AppendLine();
        sb.AppendLine($"Drafted **{volume.Narratives.Count}** contract narrative(s). " +
                      "Full narratives are available via `POST /api/pastperformance/volume`.");

        if (volume.FlaggedGaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### ⚠️ Gaps Flagged");
            foreach (var g in volume.FlaggedGaps) sb.AppendLine($"- {g}");
        }

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = topContracts,
            DraftedSection    = volume,
            Citations         = topContracts.Select(BuildCitation).ToList(),
            Warnings          = volume.FlaggedGaps
        };
    }

    private async Task<PastPerformanceResponse> HandlePortfolioSummaryAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked, CancellationToken ct)
    {
        var json   = JsonSerializer.Serialize(ranked.Take(10), new JsonSerializerOptions { WriteIndented = true });
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.PortfolioSummarySystem),
            new UserChatMessage(PastPerformancePrompts.PortfolioSummaryUserTemplate
                .Replace("{opportunityContext}", query.RawQuestion)
                .Replace("{contractsJson}",     json))
        };

        var resp = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 800, Temperature = 0.2f }, ct);

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = resp.Value.Content[0].Text,
            RelevantContracts = ranked.Take(10).ToList(),
            Citations         = ranked.Take(10).Select(BuildCitation).ToList()
        };
    }

    private async Task<PastPerformanceResponse> HandleGapAnalysisAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked,
        string requirements, CancellationToken ct)
    {
        var json   = JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true });
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.GapAnalysisSystem),
            new UserChatMessage(PastPerformancePrompts.GapAnalysisUserTemplate
                .Replace("{requirements}",  requirements)
                .Replace("{contractsJson}", json))
        };

        var resp = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 600, Temperature = 0.1f }, ct);

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = resp.Value.Content[0].Text,
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private static PastPerformanceResponse HandleFindReferences(
        PastPerformanceQuery query, List<ContractRecord> ranked)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 📞 Contracting Officer References");
        sb.AppendLine();

        foreach (var c in ranked.Where(c =>
            !string.IsNullOrEmpty(c.ContractingOfficer) ||
            !string.IsNullOrEmpty(c.ContractingOfficerEmail)))
        {
            sb.AppendLine($"### {c.ContractNumber} — {c.AgencyName}");
            sb.AppendLine($"**{c.Title}** | ${c.FinalObligatedValue ?? c.ContractValue:N0}");
            sb.AppendLine($"- **CO:** {c.ContractingOfficer ?? "N/A"} | {c.ContractingOfficerPhone ?? "N/A"} | {c.ContractingOfficerEmail ?? "N/A"}");
            sb.AppendLine($"- **COR:** {c.COR ?? "N/A"} | {c.CORPhone ?? "N/A"} | {c.COREmail ?? "N/A"}");
            sb.AppendLine();
        }

        if (sb.Length < 60)
            sb.AppendLine("No CO/COR contact information found. Retrieve from CPARS.gov or your contracts system.");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private static PastPerformanceResponse HandleExtractCpars(
        PastPerformanceQuery query, List<ContractRecord> ranked)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ⭐ CPARS Ratings");
        sb.AppendLine();
        sb.AppendLine("| Contract | Agency | Overall | Quality | Schedule | Cost Control | Management |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var c in ranked)
            sb.AppendLine($"| {c.ContractNumber} | {c.AgencyAcronym ?? c.AgencyName} " +
                          $"| {c.CPARSRatingOverall ?? "—"} | {c.CPARSRatingQuality ?? "—"} " +
                          $"| {c.CPARSRatingSchedule ?? "—"} | {c.CPARSRatingCostControl ?? "—"} " +
                          $"| {c.CPARSRatingManagement ?? "—"} |");

        var missing = ranked.Count(c => string.IsNullOrEmpty(c.CPARSRatingOverall));
        if (missing > 0)
            sb.AppendLine($"\n⚠️ {missing} contract(s) have no CPARS data — retrieve from [CPARS.gov](https://www.cpars.gov).");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private static PastPerformanceResponse HandleFindKeyPersonnel(
        PastPerformanceQuery query, List<ContractRecord> ranked)
    {
        var sb   = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("## 👤 Key Personnel with Relevant Experience").AppendLine();

        foreach (var c in ranked)
        foreach (var p in c.KeyPersonnel)
        {
            if (!seen.Add(p.Name)) continue;
            sb.AppendLine($"**{p.Name}** — {p.Title}");
            if (!string.IsNullOrEmpty(p.Clearance)) sb.AppendLine($"  Clearance: {p.Clearance}");
            if (!string.IsNullOrEmpty(p.Role))      sb.AppendLine($"  Role: {p.Role}");
            sb.AppendLine($"  Contract: {c.ContractNumber} | {c.AgencyName} | {c.Title}").AppendLine();
        }

        if (seen.Count == 0)
            sb.AppendLine("No key personnel data found. Add personnel info to your past performance records.");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private async Task<PastPerformanceResponse> HandleGeneralAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var c in ranked.Take(query.TopK))
        {
            sb.AppendLine($"Contract: {c.ContractNumber} | {c.AgencyName} | {c.Title}");
            sb.AppendLine($"Value: ${c.FinalObligatedValue ?? c.ContractValue:N0} | " +
                          $"Period: {c.StartDate} – {(c.IsOngoing ? "Ongoing" : c.EndDate?.ToString())}");
            sb.AppendLine($"CPARS: {c.CPARSRatingOverall ?? "N/A"}");
            if (c.KeyAccomplishments.Count > 0)
                sb.AppendLine($"Accomplishments: {string.Join("; ", c.KeyAccomplishments.Take(3))}");
            sb.AppendLine();
        }

        var client   = _openAi.GetChatClient(_aoai.ChatDeployment);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                You are a GovCon past performance expert. Answer the user's question
                using only the contract data provided. Be specific, cite contract numbers,
                and format your response with markdown for readability.
                """),
            new UserChatMessage($"Context:\n{sb}\n\nQuestion: {query.RawQuestion}")
        };

        var resp = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 800, Temperature = 0.2f }, ct);

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = resp.Value.Content[0].Text,
            RelevantContracts = ranked.Take(query.TopK).ToList(),
            Citations         = ranked.Take(query.TopK).Select(BuildCitation).ToList()
        };
    }

    private static string BuildCitation(ContractRecord c) =>
        $"[{c.ContractNumber}] {c.Title} — {c.AgencyName}" +
        (string.IsNullOrEmpty(c.SourceDocumentUrl) ? "" : $" ({c.SourceDocumentUrl})");
}
