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
/// Source-aware Past Performance orchestrator.
///
/// Searches across all assigned RAG systems (which may be backed by SharePoint,
/// SQL, Excel, Deltek, or Custom connectors), extracts ContractRecords using
/// source-appropriate strategies, applies GovCon scoring, and routes by intent.
///
/// Every response carries DataSourcesSearched so callers know which sources
/// contributed to the answer.
/// </summary>
public sealed class PastPerformanceOrchestrator : IPastPerformanceOrchestrator
{
    private readonly IQueryParser      _queryParser;
    private readonly IRagOrchestrator  _ragOrchestrator;
    private readonly IContractExtractor _extractor;
    private readonly IRelevanceScorer  _scorer;
    private readonly IProposalDrafter  _drafter;
    private readonly AzureOpenAIClient _openAi;
    private readonly AzureOpenAIOptions _aoai;
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

        var systemNames = agentOpts.Value.SystemNames.Count > 0
            ? agentOpts.Value.SystemNames
            : (IReadOnlyList<string>)["PastPerformance"];

        _ragOrchestrator = orchestratorFactory.Create(systemNames);

        _logger.LogInformation(
            "[PP] Orchestrator covers systems [{S}]",
            string.Join(", ", systemNames));
    }

    public async Task<PastPerformanceResponse> HandleAsync(
        string userMessage, CancellationToken ct = default)
    {
        _logger.LogInformation("[PP] Request: {M}", userMessage);

        // 1. Parse intent + filters (now includes ConnectorTypeFilter, DataSourceFilter)
        var query = await _queryParser.ParseAsync(userMessage, ct);
        _logger.LogDebug("[PP] Intent={I}, Filters: connectors=[{C}] sources=[{S}]",
            query.Intent,
            string.Join(",", query.ConnectorTypeFilter),
            string.Join(",", query.DataSourceFilter));

        // 2. Vector search across all assigned systems
        var ragResponse = await _ragOrchestrator.AskAsync(query.SemanticQuery, ct);
        var chunks      = ragResponse.Sources;

        // 3. Collect which data sources were actually searched
        var dataSourcesSearched = chunks
            .Select(c => c.Chunk.DataSourceName)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        _logger.LogInformation("[PP] {N} chunks from sources: [{S}]",
            chunks.Count, string.Join(", ", dataSourcesSearched));

        if (chunks.Count == 0)
        {
            return new PastPerformanceResponse
            {
                Query              = query,
                Answer             = BuildNoResultsMessage(query),
                DataSourcesSearched = dataSourcesSearched,
                Warnings = ["No matching records found. Ensure all configured data sources are indexed."]
            };
        }

        // 4. Source-aware extraction (documents → LLM, structured → direct mapping / LLM enrichment)
        var contracts = await _extractor.ExtractAsync(chunks, ct);

        // 5. Score and rank (honours connector-type and data-source filters from query)
        var ranked = _scorer.ScoreAndRank(contracts, query);

        // 6. Route by intent
        var response = query.Intent switch
        {
            QueryIntent.GenerateVolumeSection =>
                await HandleVolumeDraftAsync(query, ranked, userMessage, dataSourcesSearched, ct),
            QueryIntent.SummarisePortfolio =>
                await HandlePortfolioSummaryAsync(query, ranked, dataSourcesSearched, ct),
            QueryIntent.IdentifyGaps =>
                await HandleGapAnalysisAsync(query, ranked, userMessage, dataSourcesSearched, ct),
            QueryIntent.FindReferences =>
                HandleFindReferences(query, ranked, dataSourcesSearched),
            QueryIntent.ExtractCPARSRatings =>
                HandleExtractCpars(query, ranked, dataSourcesSearched),
            QueryIntent.FindKeyPersonnel =>
                HandleFindKeyPersonnel(query, ranked, dataSourcesSearched),
            _ =>
                await HandleGeneralAsync(query, ranked, dataSourcesSearched, ct)
        };

        return response with { DataSourcesSearched = dataSourcesSearched };
    }

    // ── Intent handlers ───────────────────────────────────────────────────────

    private async Task<PastPerformanceResponse> HandleVolumeDraftAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked,
        string solicitationContext, List<string> dataSources, CancellationToken ct)
    {
        var top    = ranked.Take(5).ToList();
        var volume = await _drafter.DraftVolumeAsync(top, solicitationContext, ct);

        var sb = new StringBuilder();
        sb.AppendLine("## 📋 Past Performance Volume Draft");
        sb.AppendLine();
        sb.AppendLine($"**Executive Summary:** {volume.ExecutiveSummary}");
        sb.AppendLine();
        sb.AppendLine($"Drafted **{volume.Narratives.Count}** narrative(s) from **{dataSources.Count}** source(s): " +
                      string.Join(", ", dataSources));
        sb.AppendLine("Full narratives available via `POST /api/pastperformance/volume`.");

        if (volume.FlaggedGaps.Count > 0)
        {
            sb.AppendLine().AppendLine("### ⚠️ Gaps Flagged");
            foreach (var g in volume.FlaggedGaps) sb.AppendLine($"- {g}");
        }

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = top,
            DraftedSection    = volume,
            Citations         = top.Select(BuildCitation).ToList(),
            Warnings          = volume.FlaggedGaps
        };
    }

    private async Task<PastPerformanceResponse> HandlePortfolioSummaryAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked,
        List<string> dataSources, CancellationToken ct)
    {
        var json   = JsonSerializer.Serialize(ranked.Take(10), new JsonSerializerOptions { WriteIndented = true });
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.PortfolioSummarySystem),
            new UserChatMessage(PastPerformancePrompts.PortfolioSummaryUserTemplate
                .Replace("{opportunityContext}", query.RawQuestion)
                .Replace("{dataSources}",       string.Join(", ", dataSources))
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
        string requirements, List<string> dataSources, CancellationToken ct)
    {
        var json   = JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true });
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.GapAnalysisSystem),
            new UserChatMessage(PastPerformancePrompts.GapAnalysisUserTemplate
                .Replace("{requirements}",  requirements)
                .Replace("{dataSources}",   string.Join(", ", dataSources))
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
        PastPerformanceQuery query, List<ContractRecord> ranked, List<string> dataSources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 📞 Contracting Officer References");
        sb.AppendLine($"*Sources searched: {string.Join(", ", dataSources)}*").AppendLine();

        foreach (var c in ranked.Where(c =>
            !string.IsNullOrEmpty(c.ContractingOfficer) ||
            !string.IsNullOrEmpty(c.ContractingOfficerEmail)))
        {
            sb.AppendLine($"### {c.ContractNumber} — {c.AgencyName}");
            sb.AppendLine($"**{c.Title}** | ${c.FinalObligatedValue ?? c.ContractValue:N0} | *Source: {c.DataSourceName}*");
            sb.AppendLine($"- **CO:** {c.ContractingOfficer ?? "N/A"} | {c.ContractingOfficerPhone ?? "N/A"} | {c.ContractingOfficerEmail ?? "N/A"}");
            sb.AppendLine($"- **COR:** {c.COR ?? "N/A"} | {c.CORPhone ?? "N/A"} | {c.COREmail ?? "N/A"}");
            sb.AppendLine();
        }

        if (sb.Length < 80)
            sb.AppendLine("No CO/COR contacts found. Retrieve from CPARS.gov or your contracts system.");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private static PastPerformanceResponse HandleExtractCpars(
        PastPerformanceQuery query, List<ContractRecord> ranked, List<string> dataSources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ⭐ CPARS Ratings");
        sb.AppendLine($"*Sources searched: {string.Join(", ", dataSources)}*").AppendLine();
        sb.AppendLine("| Contract | Agency | Source | Overall | Quality | Schedule | Cost Control | Management |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var c in ranked)
            sb.AppendLine($"| {c.ContractNumber} | {c.AgencyAcronym ?? c.AgencyName} " +
                          $"| {c.DataSourceName} " +
                          $"| {c.CPARSRatingOverall ?? "—"} | {c.CPARSRatingQuality ?? "—"} " +
                          $"| {c.CPARSRatingSchedule ?? "—"} | {c.CPARSRatingCostControl ?? "—"} " +
                          $"| {c.CPARSRatingManagement ?? "—"} |");

        var missing = ranked.Count(c => string.IsNullOrEmpty(c.CPARSRatingOverall));
        if (missing > 0)
            sb.AppendLine($"\n⚠️ {missing} record(s) have no CPARS data — retrieve from [CPARS.gov](https://www.cpars.gov).");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private static PastPerformanceResponse HandleFindKeyPersonnel(
        PastPerformanceQuery query, List<ContractRecord> ranked, List<string> dataSources)
    {
        var sb   = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("## 👤 Key Personnel with Relevant Experience");
        sb.AppendLine($"*Sources searched: {string.Join(", ", dataSources)}*").AppendLine();

        foreach (var c in ranked)
        foreach (var p in c.KeyPersonnel)
        {
            if (!seen.Add(p.Name)) continue;
            sb.AppendLine($"**{p.Name}** — {p.Title}");
            if (!string.IsNullOrEmpty(p.Clearance)) sb.AppendLine($"  Clearance: {p.Clearance}");
            if (!string.IsNullOrEmpty(p.Role))      sb.AppendLine($"  Role: {p.Role}");
            sb.AppendLine($"  Contract: {c.ContractNumber} | {c.AgencyName} | {c.Title} | *{c.DataSourceName}*").AppendLine();
        }

        if (seen.Count == 0)
            sb.AppendLine("No key personnel data found. Add personnel info to your records.");

        return new PastPerformanceResponse
        {
            Query             = query,
            Answer            = sb.ToString(),
            RelevantContracts = ranked,
            Citations         = ranked.Select(BuildCitation).ToList()
        };
    }

    private async Task<PastPerformanceResponse> HandleGeneralAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked,
        List<string> dataSources, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sources searched: {string.Join(", ", dataSources)}").AppendLine();

        foreach (var c in ranked.Take(query.TopK))
        {
            sb.AppendLine($"Contract: {c.ContractNumber} | {c.AgencyName} | {c.Title} | Source: {c.DataSourceName} ({c.ConnectorType})");
            sb.AppendLine($"Value: ${c.FinalObligatedValue ?? c.ContractValue:N0} | Period: {c.StartDate} – {(c.IsOngoing ? "Ongoing" : c.EndDate?.ToString())}");
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
                using only the contract data provided. Be specific, cite contract numbers
                and source names, and format your response with markdown.
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
        $"[{c.ContractNumber}] {c.Title} — {c.AgencyName} [{c.DataSourceName}/{c.ConnectorType}]" +
        (string.IsNullOrEmpty(c.SourceDocumentUrl) ? "" : $" ({c.SourceDocumentUrl})");

    private static string BuildNoResultsMessage(PastPerformanceQuery q)
    {
        var sb = new StringBuilder();
        sb.AppendLine("I could not find any relevant past performance records for your query.");

        if (q.ConnectorTypeFilter.Count > 0)
            sb.AppendLine($"  Filter active: connector types = [{string.Join(", ", q.ConnectorTypeFilter)}]");
        if (q.DataSourceFilter.Count > 0)
            sb.AppendLine($"  Filter active: data sources = [{string.Join(", ", q.DataSourceFilter)}]");

        sb.AppendLine("Please ensure all configured data sources are indexed (`POST /api/index/full`).");
        return sb.ToString();
    }
}
