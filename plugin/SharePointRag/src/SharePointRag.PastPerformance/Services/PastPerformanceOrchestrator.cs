using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Extensions;
using SharePointRag.Core.Interfaces;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Plugins;
using SharePointRag.PastPerformance.Prompts;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Source-aware Past Performance orchestrator with optional external AI plugin support.
///
/// Pipeline per request:
///
///   1. LlmQueryParser      → structured intent + filters
///   2. RagOrchestrator     → vector search across all assigned systems
///   3. IPluginRouter       → (optional) GPT-4o tool-calling routes to 0..N external AI systems
///                            Results are merged into the context grounding
///   4. ContractExtractor   → source-aware: documents→LLM, structured→direct mapping
///   5. RelevanceScorer     → GovCon weights + connector-type bonuses
///   6. Intent Router       → GenerateVolumeSection / SummarisePortfolio / etc.
///
/// Plugin enrichment: plugin responses are included in the context passed to every
/// LLM call in steps 4-6, so the final answer is grounded in BOTH internal knowledge
/// (RAG) and external AI system outputs.
/// </summary>
public sealed class PastPerformanceOrchestrator : IPastPerformanceOrchestrator
{
    private readonly IQueryParser      _queryParser;
    private readonly IRagOrchestrator  _ragOrchestrator;
    private readonly IContractExtractor _extractor;
    private readonly IRelevanceScorer  _scorer;
    private readonly IProposalDrafter  _drafter;
    private readonly IPluginRouter     _pluginRouter;
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
        IPluginRouter pluginRouter,
        AzureOpenAIClient openAi,
        IOptions<AzureOpenAIOptions> aoaiOpts,
        ILogger<PastPerformanceOrchestrator> logger)
    {
        _queryParser  = queryParser;
        _extractor    = extractor;
        _scorer       = scorer;
        _drafter      = drafter;
        _pluginRouter = pluginRouter;
        _openAi       = openAi;
        _aoai         = aoaiOpts.Value;
        _logger       = logger;

        var systemNames = agentOpts.Value.SystemNames.Count > 0
            ? agentOpts.Value.SystemNames
            : (IReadOnlyList<string>)["PastPerformance"];

        _ragOrchestrator = orchestratorFactory.Create(systemNames);

        _logger.LogInformation(
            "[PP] Orchestrator covers systems [{S}], plugins enabled: {P}",
            string.Join(", ", systemNames), pluginRouter.HasPlugins);
    }

    public async Task<PastPerformanceResponse> HandleAsync(
        string userMessage, CancellationToken ct = default)
    {
        _logger.LogInformation("[PP] Request: {M}", userMessage);

        // ── 1. Parse intent ───────────────────────────────────────────────────
        var query = await _queryParser.ParseAsync(userMessage, ct);
        _logger.LogDebug("[PP] Intent={I}, Connectors=[{C}] Sources=[{S}]",
            query.Intent,
            string.Join(",", query.ConnectorTypeFilter),
            string.Join(",", query.DataSourceFilter));

        // ── 2. RAG vector search ──────────────────────────────────────────────
        var ragResponse = await _ragOrchestrator.AskAsync(query.SemanticQuery, ct);
        var chunks      = ragResponse.Sources;

        var dataSourcesSearched = chunks
            .Select(c => c.Chunk.DataSourceName)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        _logger.LogInformation("[PP] {N} chunks from sources: [{S}]",
            chunks.Count, string.Join(", ", dataSourcesSearched));

        // ── 3. External AI plugin routing (parallel with a compact RAG summary) ─
        PluginRoutingResult pluginResult = new();
        if (_pluginRouter.HasPlugins)
        {
            // Build a compact summary of what RAG found to give plugins context
            var ragContextSummary = BuildCompactRagSummary(chunks);

            var pluginRequest = new ExternalAiRequest
            {
                UserQuestion    = userMessage,
                SemanticQuery   = query.SemanticQuery,
                FocusedQuestion = userMessage,     // refined per-plugin by the router
                ExistingContext = ragContextSummary,
                Intent          = query.Intent.ToString(),
                QueryFilters    = BuildQueryFilters(query)
            };

            pluginResult = await _pluginRouter.RouteAsync(pluginRequest, ct);

            if (pluginResult.AnyInvoked)
                _logger.LogInformation(
                    "[PP] Plugins invoked: [{P}], successes: {S}",
                    string.Join(", ", pluginResult.Responses.Select(r => r.PluginName)),
                    pluginResult.Successes.Count());
        }

        // ── No results from RAG or plugins ────────────────────────────────────
        if (chunks.Count == 0 && !pluginResult.Successes.Any())
        {
            return new PastPerformanceResponse
            {
                Query               = query,
                Answer              = BuildNoResultsMessage(query),
                DataSourcesSearched = dataSourcesSearched,
                PluginResponses     = [.. pluginResult.Responses],
                Warnings = ["No matching records found. Ensure all data sources are indexed."]
            };
        }

        // ── 4. Source-aware extraction ────────────────────────────────────────
        // Pass plugin context so the extractor can use it as supplemental grounding
        var pluginContext = BuildPluginContext(pluginResult);
        var contracts     = await _extractor.ExtractAsync(chunks, ct);

        // ── 5. Score and rank ─────────────────────────────────────────────────
        var ranked = _scorer.ScoreAndRank(contracts, query);

        // ── 6. Route by intent ────────────────────────────────────────────────
        var response = query.Intent switch
        {
            QueryIntent.GenerateVolumeSection =>
                await HandleVolumeDraftAsync(query, ranked, userMessage, dataSourcesSearched, pluginContext, ct),
            QueryIntent.SummarisePortfolio =>
                await HandlePortfolioSummaryAsync(query, ranked, dataSourcesSearched, pluginContext, ct),
            QueryIntent.IdentifyGaps =>
                await HandleGapAnalysisAsync(query, ranked, userMessage, dataSourcesSearched, pluginContext, ct),
            QueryIntent.FindReferences =>
                HandleFindReferences(query, ranked, dataSourcesSearched),
            QueryIntent.ExtractCPARSRatings =>
                HandleExtractCpars(query, ranked, dataSourcesSearched),
            QueryIntent.FindKeyPersonnel =>
                HandleFindKeyPersonnel(query, ranked, dataSourcesSearched),
            _ =>
                await HandleGeneralAsync(query, ranked, dataSourcesSearched, pluginContext, ct)
        };

        return response with
        {
            DataSourcesSearched = dataSourcesSearched,
            PluginResponses     = [.. pluginResult.Responses]
        };
    }

    // ── Intent handlers ───────────────────────────────────────────────────────

    private async Task<PastPerformanceResponse> HandleVolumeDraftAsync(
        PastPerformanceQuery query, List<ContractRecord> ranked,
        string solicitationContext, List<string> dataSources,
        string pluginContext, CancellationToken ct)
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

        if (!string.IsNullOrEmpty(pluginContext))
        {
            sb.AppendLine().AppendLine("### 🔗 External AI Insights");
            sb.AppendLine(pluginContext);
        }

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
        List<string> dataSources, string pluginContext, CancellationToken ct)
    {
        var json   = JsonSerializer.Serialize(ranked.Take(10), new JsonSerializerOptions { WriteIndented = true });
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);

        var userContent = PastPerformancePrompts.PortfolioSummaryUserTemplate
            .Replace("{opportunityContext}", query.RawQuestion)
            .Replace("{dataSources}",       string.Join(", ", dataSources))
            .Replace("{contractsJson}",     json);

        if (!string.IsNullOrEmpty(pluginContext))
            userContent += $"\n\nAdditional context from external AI systems:\n{pluginContext}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.PortfolioSummarySystem),
            new UserChatMessage(userContent)
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
        string requirements, List<string> dataSources,
        string pluginContext, CancellationToken ct)
    {
        var json   = JsonSerializer.Serialize(ranked, new JsonSerializerOptions { WriteIndented = true });
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);

        var userContent = PastPerformancePrompts.GapAnalysisUserTemplate
            .Replace("{requirements}",  requirements)
            .Replace("{dataSources}",   string.Join(", ", dataSources))
            .Replace("{contractsJson}", json);

        if (!string.IsNullOrEmpty(pluginContext))
            userContent += $"\n\nExternal AI context (incorporate into gap analysis):\n{pluginContext}";

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.GapAnalysisSystem),
            new UserChatMessage(userContent)
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
        List<string> dataSources, string pluginContext, CancellationToken ct)
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

        if (!string.IsNullOrEmpty(pluginContext))
        {
            sb.AppendLine("--- External AI context ---");
            sb.AppendLine(pluginContext);
        }

        var client   = _openAi.GetChatClient(_aoai.ChatDeployment);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                You are a GovCon past performance expert. Answer the user's question
                using the contract data and any additional external AI context provided.
                Be specific, cite contract numbers and source names, and use markdown.
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

    private static string BuildCompactRagSummary(
        IReadOnlyList<SharePointRag.Core.Models.RetrievedChunk> chunks)
    {
        if (chunks.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var c in chunks.Take(5))
            sb.AppendLine($"[{c.Chunk.DataSourceName}] {c.Chunk.Title}: {c.Chunk.Content[..Math.Min(200, c.Chunk.Content.Length)]}…");
        return sb.ToString();
    }

    private static string BuildPluginContext(PluginRoutingResult pluginResult)
    {
        if (!pluginResult.AnyInvoked) return string.Empty;
        var sb = new StringBuilder();
        foreach (var r in pluginResult.Successes)
        {
            sb.AppendLine($"**{r.PluginName}** ({r.PluginDescription}):");
            sb.AppendLine(r.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static Dictionary<string, string> BuildQueryFilters(PastPerformanceQuery query)
    {
        var filters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(query.AgencyFilter))     filters["agency"]   = query.AgencyFilter;
        if (!string.IsNullOrEmpty(query.NaicsFilter))      filters["naics"]    = query.NaicsFilter;
        if (!string.IsNullOrEmpty(query.KeywordFilter))    filters["keyword"]  = query.KeywordFilter;
        if (query.RecencyYearsFilter.HasValue)             filters["recency"]  = $"{query.RecencyYearsFilter}y";
        return filters;
    }

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
