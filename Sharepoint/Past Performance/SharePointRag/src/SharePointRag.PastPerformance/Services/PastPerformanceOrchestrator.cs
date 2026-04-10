using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
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
/// Pipeline per request:
///   1. Parse intent         → <see cref="IQueryParser"/>
///   2. Embed semantic query → <see cref="IEmbeddingService"/>
///   3. HNSW vector search   → <see cref="IVectorStore"/>
///   4. Extract contracts    → <see cref="IContractExtractor"/>
///   5. Score and rank       → <see cref="IRelevanceScorer"/>
///   6. Route by intent:
///      a. GenerateVolumeSection → <see cref="IProposalDrafter"/> full volume
///      b. FindReferences / ExtractCPARSRatings / FindKeyPersonnel → focused answer
///      c. SummarisePortfolio / IdentifyGaps → LLM synthesis
///      d. FindSimilarContracts / General → ranked list + brief answer
/// </summary>
public sealed class PastPerformanceOrchestrator(
    IQueryParser queryParser,
    IEmbeddingService embedder,
    IVectorStore vectorStore,
    IContractExtractor extractor,
    IRelevanceScorer scorer,
    IProposalDrafter drafter,
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    IOptions<SharpCoreDbOptions> scdbOpts,
    ILogger<PastPerformanceOrchestrator> logger) : IPastPerformanceOrchestrator
{
    private readonly AzureOpenAIOptions _aoai = aoaiOpts.Value;
    private readonly SharpCoreDbOptions _scdb = scdbOpts.Value;

    public async Task<PastPerformanceResponse> HandleAsync(
        string userMessage, CancellationToken ct = default)
    {
        logger.LogInformation("PastPerformance request: {M}", userMessage);

        // ── Step 1: Parse intent ──────────────────────────────────────────────
        var query = await queryParser.ParseAsync(userMessage, ct);
        logger.LogDebug("Intent={I}, SemanticQuery={Q}", query.Intent, query.SemanticQuery);

        // ── Step 2: Embed ─────────────────────────────────────────────────────
        var queryVector = await embedder.EmbedAsync(query.SemanticQuery, ct);

        // ── Step 3: Vector search ─────────────────────────────────────────────
        var topK    = Math.Clamp(query.TopK, 3, 20);
        var chunks  = await vectorStore.SearchAsync(queryVector, topK, ct);

        if (chunks.Count == 0)
        {
            return new PastPerformanceResponse
            {
                Query    = query,
                Answer   = "I could not find any relevant past performance records in the document library for your query. " +
                           "Please ensure your contracts and performance reports have been indexed.",
                Warnings = ["No matching documents found in the vector index."]
            };
        }

        // ── Step 4: Extract structured contract records ───────────────────────
        var contracts = await extractor.ExtractAsync(chunks, ct);

        // ── Step 5: Score and rank ────────────────────────────────────────────
        var ranked = scorer.ScoreAndRank(contracts, query);

        // ── Step 6: Route by intent ───────────────────────────────────────────
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
                HandleFindKeyPersonnel(query, ranked, userMessage),

            _ => // FindSimilarContracts + General
                await HandleGeneralAsync(query, ranked, ct)
        };
    }

    // ── Intent handlers ───────────────────────────────────────────────────────

    private async Task<PastPerformanceResponse> HandleVolumeDraftAsync(
        PastPerformanceQuery query,
        List<ContractRecord> ranked,
        string solicitationContext,
        CancellationToken ct)
    {
        var topContracts = ranked.Take(5).ToList();
        var volume = await drafter.DraftVolumeAsync(topContracts, solicitationContext, ct);

        var sb = new StringBuilder();
        sb.AppendLine("## 📋 Past Performance Volume Draft");
        sb.AppendLine();
        sb.AppendLine($"**Executive Summary:** {volume.ExecutiveSummary}");
        sb.AppendLine();
        sb.AppendLine($"Drafted **{volume.Narratives.Count}** contract narrative(s). " +
                      "Full narratives are available via the REST API (`/api/pastperformance/volume`).");

        if (volume.FlaggedGaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### ⚠️ Gaps Flagged");
            foreach (var g in volume.FlaggedGaps) sb.AppendLine($"- {g}");
        }

        return new PastPerformanceResponse
        {
            Query              = query,
            Answer             = sb.ToString(),
            RelevantContracts  = topContracts,
            DraftedSection     = volume,
            Citations          = topContracts.Select(BuildCitation).ToList(),
            Warnings           = volume.FlaggedGaps
        };
    }

    private async Task<PastPerformanceResponse> HandlePortfolioSummaryAsync(
        PastPerformanceQuery query,
        List<ContractRecord> ranked,
        CancellationToken ct)
    {
        var contractsJson = JsonSerializer.Serialize(ranked.Take(10), new JsonSerializerOptions { WriteIndented = true });
        var client        = openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.PortfolioSummarySystem),
            new UserChatMessage(PastPerformancePrompts.PortfolioSummaryUserTemplate
                .Replace("{opportunityContext}", query.RawQuestion)
                .Replace("{contractsJson}",     contractsJson))
        };

        var resp   = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 800, Temperature = 0.2f }, ct);
        var answer = resp.Value.Content[0].Text;

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = answer,
            RelevantContracts = ranked.Take(10).ToList(),
            Citations         = ranked.Take(10).Select(BuildCitation).ToList()
        };
    }

    private async Task<PastPerformanceResponse> HandleGapAnalysisAsync(
        PastPerformanceQuery query,
        List<ContractRecord> ranked,
        string requirements,
        CancellationToken ct)
    {
        var contractsJson = JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true });
        var client        = openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.GapAnalysisSystem),
            new UserChatMessage(PastPerformancePrompts.GapAnalysisUserTemplate
                .Replace("{requirements}",  requirements)
                .Replace("{contractsJson}", contractsJson))
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

        if (sb.Length < 50)
            sb.AppendLine("No CO/COR contact information found in the indexed documents. Retrieve from CPARS.gov or your contracts management system.");

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
        {
            sb.AppendLine($"| {c.ContractNumber} | {c.AgencyAcronym ?? c.AgencyName} " +
                          $"| {c.CPARSRatingOverall ?? "—"} " +
                          $"| {c.CPARSRatingQuality ?? "—"} " +
                          $"| {c.CPARSRatingSchedule ?? "—"} " +
                          $"| {c.CPARSRatingCostControl ?? "—"} " +
                          $"| {c.CPARSRatingManagement ?? "—"} |");
        }

        var noCpars = ranked.Count(c => string.IsNullOrEmpty(c.CPARSRatingOverall));
        if (noCpars > 0)
            sb.AppendLine($"\n⚠️ {noCpars} contract(s) have no CPARS data in the index — retrieve from [CPARS.gov](https://www.cpars.gov).");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private static PastPerformanceResponse HandleFindKeyPersonnel(
        PastPerformanceQuery query, List<ContractRecord> ranked, string userMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 👤 Key Personnel with Relevant Experience");
        sb.AppendLine();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ranked)
        foreach (var p in c.KeyPersonnel)
        {
            if (!seen.Add(p.Name)) continue;
            sb.AppendLine($"**{p.Name}** — {p.Title}");
            if (!string.IsNullOrEmpty(p.Clearance)) sb.AppendLine($"  Clearance: {p.Clearance}");
            if (!string.IsNullOrEmpty(p.Role))      sb.AppendLine($"  Role: {p.Role}");
            sb.AppendLine($"  Related contract: {c.ContractNumber} | {c.AgencyName} | {c.Title}");
            sb.AppendLine();
        }

        if (seen.Count == 0)
            sb.AppendLine("No key personnel data found in the indexed documents. " +
                          "Add personnel information to your past performance records.");

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
        // Build a context string from top contracts and let GPT answer freely
        var sb = new StringBuilder();
        foreach (var c in ranked.Take(query.TopK))
        {
            sb.AppendLine($"Contract: {c.ContractNumber} | {c.AgencyName} | {c.Title}");
            sb.AppendLine($"Value: ${c.FinalObligatedValue ?? c.ContractValue:N0} | Period: {c.StartDate} – {(c.IsOngoing ? "Ongoing" : c.EndDate?.ToString())}");
            sb.AppendLine($"CPARS: {c.CPARSRatingOverall ?? "N/A"}");
            if (c.KeyAccomplishments.Count > 0)
                sb.AppendLine($"Accomplishments: {string.Join("; ", c.KeyAccomplishments.Take(3))}");
            sb.AppendLine();
        }

        var client = openAi.GetChatClient(_aoai.ChatDeployment);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                You are a GovCon past performance expert. Answer the user's question
                using only the contract data provided. Be specific and cite contract numbers.
                Format your response with markdown for readability.
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildCitation(ContractRecord c) =>
        $"[{c.ContractNumber}] {c.Title} — {c.AgencyName}" +
        (string.IsNullOrEmpty(c.SourceDocumentUrl) ? string.Empty : $" ({c.SourceDocumentUrl})");
}
