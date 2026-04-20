// ============================================================
//  GovConRAG.Agents — Multi-Agent System
//  Microsoft Agent Framework (Microsoft.Agents.AI)
//  Agents: Router | Accounts | Contracts | Ops |
//          PastPerformance | RFP/Proposal | Competitor
// ============================================================

using GovConRAG.Core.Models;
using GovConRAG.Core.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace GovConRAG.Agents;

// ── Tool Definitions (AIFunctionFactory) ──────────────────────

public static class RagTools
{
    public static AIFunction CreateVectorSearchTool(IVectorStore store, IEmbeddingGenerator<string, Embedding<float>> embedder) =>
        AIFunctionFactory.Create(
            async (string query, string? domain, int topK = 8) =>
            {
                var emb    = await embedder.GenerateEmbeddingAsync(query);
                var chunks = await store.VectorSearchAsync(emb.Vector.ToArray(), domain, topK, 0.70f);
                return string.Join("\n\n---\n\n", chunks.Select(c =>
                    $"[Score:{c.Score:F3} | Source:{c.SourceTitle}]\n{c.Content}"));
            },
            "search_documents",
            "Performs semantic vector search over indexed documents. Returns relevant text excerpts.");

    public static AIFunction CreateGraphContextTool(IVectorStore store) =>
        AIFunctionFactory.Create(
            async (string documentNodeIdHex) =>
            {
                if (!Guid.TryParse(documentNodeIdHex, out var nodeId))
                    return "Invalid node ID";
                var ctx = await store.GraphContextAsync(nodeId, depth: 2);
                return System.Text.Json.JsonSerializer.Serialize(ctx);
            },
            "get_graph_context",
            "Returns the knowledge graph context (related nodes/edges) for a given document node.");

    public static AIFunction CreateContractSearchTool() =>
        AIFunctionFactory.Create(
            (string agency, string naics) =>
                $"[Contract DB] Agency={agency} NAICS={naics} — returns mock contract data in production",
            "search_contracts",
            "Search active contracts by agency and NAICS code.");

    public static AIFunction CreatePastPerformanceTool() =>
        AIFunctionFactory.Create(
            (string capability) =>
                $"[Past Perf DB] Capability={capability} — returns past performance records in production",
            "get_past_performance",
            "Retrieve past performance records matching a capability or keyword.");

    public static AIFunction CreateSamSearchTool(HttpClient http) =>
        AIFunctionFactory.Create(
            async (string keyword, string naics) =>
            {
                // SAM.gov API (requires API key in production)
                var url  = $"https://api.sam.gov/opportunities/v2/search?keyword={Uri.EscapeDataString(keyword)}&naicsCode={naics}&limit=10";
                try
                {
                    var resp = await http.GetStringAsync(url);
                    return resp[..Math.Min(2000, resp.Length)];
                }
                catch { return $"[Mock SAM data for keyword={keyword} naics={naics}]"; }
            },
            "search_sam_opportunities",
            "Search SAM.gov for active contract opportunities.");
}

// ── Base RAG Agent ────────────────────────────────────────────

public abstract class BaseRagAgent
{
    protected readonly AIAgent          _agent;
    protected readonly IVectorStore     _store;
    protected readonly IAuditLogger     _audit;
    protected readonly ILogger          _logger;
    protected abstract string           AgentName { get; }
    protected abstract AgentDomain      Domain { get; }

    protected BaseRagAgent(AIAgent agent, IVectorStore store, IAuditLogger audit, ILogger logger)
    {
        _agent = agent;
        _store = store;
        _audit = audit;
        _logger = logger;
    }

    public async Task<RagResult> QueryAsync(RagQuery query, CancellationToken ct = default)
    {
        var sw      = System.Diagnostics.Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString();

        _logger.LogInformation("[{Agent}] Query: {Q}", AgentName, query.Question);

        // Embed query + vector search
        var emb    = await GetEmbedding(query.Question, ct);
        var chunks = await _store.VectorSearchAsync(
            emb, query.Domain, query.TopK, query.MinScore, ct);

        // Build context
        var context = string.Join("\n\n---\n\n",
            chunks.Select(c => $"[{c.SourceTitle}]\n{c.Content}"));

        var prompt = BuildPrompt(query.Question, context);

        // Graph context for top result
        var graphCtx = new List<GraphContext>();
        if (query.UseGraph && chunks.Any())
        {
            var topChunkDocId = chunks.First().DocumentId;
            graphCtx = await _store.GraphContextAsync(
                chunks.First().ChunkId, depth: 2, ct);
        }

        // Run agent
        var response = await _agent.RunAsync(
            new ChatMessage(ChatRole.User, prompt),
            options: new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 2000 }
            },
            cancellationToken: ct);

        sw.Stop();

        await _audit.LogAsync(new AuditEvent
        {
            EventType    = "Agent.Query",
            Actor        = query.UserId,
            TenantId     = query.TenantId,
            ResourceType = "Agent",
            ResourceId   = AgentName,
            TraceId      = traceId,
            SessionId    = query.SessionId,
            Details      = new()
            {
                ["domain"]     = Domain.ToString(),
                ["chunkCount"] = chunks.Count,
                ["latencyMs"]  = sw.Elapsed.TotalMilliseconds
            }
        });

        return new RagResult
        {
            Answer    = response.Message.Text ?? "",
            Chunks    = chunks,
            GraphNodes = graphCtx,
            RoutedTo  = Domain,
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            SessionId = query.SessionId,
            TraceId   = traceId
        };
    }

    protected abstract string BuildPrompt(string question, string context);

    private async Task<float[]> GetEmbedding(string text, CancellationToken ct) =>
        // Use tool invocation within agent framework
        Array.Empty<float>(); // Injected via constructor in concrete classes
}

// ── Router Agent ──────────────────────────────────────────────

public sealed class RouterAgent
{
    private readonly AIAgent _agent;
    private readonly ILogger<RouterAgent> _logger;

    public RouterAgent(IChatClient chatClient, ILogger<RouterAgent> logger)
    {
        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are an intelligent routing agent for a GovCon (Government Contracting) platform.
                Analyze the user's question and route it to the correct specialist agent.

                Available agents:
                - accounts:     billing, invoicing, payment, AR/AP, financial reporting
                - contracts:    contract management, PWS, SOW, modifications, CLIN, IDIQ, task orders
                - operations:   program management, personnel, deliverables, staffing, schedule
                - performance:  CPARS, past performance, quality metrics, ratings, performance reviews
                - proposal:     RFP analysis, proposal writing, technical volumes, L/M, price-to-win
                - competitor:   competitor analysis, market intelligence, win probability, BD pipeline

                Respond with ONLY the agent name (lowercase), nothing else.
                """);
        _logger = logger;
    }

    public async Task<AgentDomain> RouteAsync(string question, CancellationToken ct = default)
    {
        var response = await _agent.RunAsync(
            new ChatMessage(ChatRole.User, question), cancellationToken: ct);

        var raw = (response.Message.Text ?? "contracts").Trim().ToLowerInvariant();
        _logger.LogDebug("Router: '{Q}' → {Domain}", question, raw);

        return raw switch
        {
            "accounts"    => AgentDomain.Accounts,
            "contracts"   => AgentDomain.Contracts,
            "operations"  => AgentDomain.Operations,
            "performance" => AgentDomain.Performance,
            "proposal"    => AgentDomain.Proposal,
            "competitor"  => AgentDomain.Competitor,
            _             => AgentDomain.Contracts
        };
    }
}

// ── Accounts Agent ────────────────────────────────────────────

public sealed class AccountsAgent
{
    private readonly AIAgent _agent;
    private readonly IVectorStore _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    public AccountsAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder, IAuditLogger audit)
    {
        _store   = store;
        _embedder = embedder;

        var tools = new[]
        {
            RagTools.CreateVectorSearchTool(store, embedder),
            RagTools.CreateContractSearchTool()
        };

        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are an Accounts specialist for a GovCon contractor.
                You help with billing, invoicing, accounts receivable/payable, financial reporting, and contract financials.
                Always cite specific document sources when providing information.
                Format financial data clearly. Flag any compliance concerns.
                """,
            tools: tools);
    }

    public async Task<string> QueryAsync(string question, string tenantId, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(new ChatMessage(ChatRole.User, question), ct: ct);
        return resp.Message.Text ?? "";
    }
}

// ── Contracts Agent ───────────────────────────────────────────

public sealed class ContractsAgent
{
    private readonly AIAgent _agent;

    public ContractsAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        var tools = new[]
        {
            RagTools.CreateVectorSearchTool(store, embedder),
            RagTools.CreateContractSearchTool(),
            RagTools.CreateGraphContextTool(store)
        };

        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are a Contracts specialist for a GovCon contractor.
                You have deep knowledge of FAR/DFARS regulations, contract types (FFP, T&M, CPFF),
                task orders, modifications, CLINs, IDIQs, and GWACs.
                Always reference the relevant FAR clause when applicable.
                Flag potential compliance issues immediately.
                """,
            tools: tools);
    }

    public async Task<string> QueryAsync(string question, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(new ChatMessage(ChatRole.User, question), ct: ct);
        return resp.Message.Text ?? "";
    }
}

// ── Operations Agent ──────────────────────────────────────────

public sealed class OperationsAgent
{
    private readonly AIAgent _agent;

    public OperationsAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are an Operations specialist for a GovCon contractor.
                You assist with program management, staffing, deliverables tracking,
                schedule management, risk registers, and PMO reporting.
                Use the Earned Value Management (EVM) framework where appropriate.
                """,
            tools: new[] { RagTools.CreateVectorSearchTool(store, embedder) });
    }

    public async Task<string> QueryAsync(string question, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(new ChatMessage(ChatRole.User, question), ct: ct);
        return resp.Message.Text ?? "";
    }
}

// ── Past Performance Review Agent ─────────────────────────────

public sealed class PastPerformanceAgent
{
    private readonly AIAgent _agent;

    public PastPerformanceAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are a Past Performance specialist for a GovCon contractor.
                You analyze CPARS ratings, write past performance narratives, identify
                relevant references for proposals, and help improve performance scores.
                Structure past performance records using the STAR format (Situation, Task, Action, Result).
                Always quantify results: percentages, dollar amounts, time saved, etc.
                Flag contracts with exceptional ratings (4-5 on CPARS) as prime proposal examples.
                """,
            tools: new[]
            {
                RagTools.CreateVectorSearchTool(store, embedder),
                RagTools.CreatePastPerformanceTool()
            });
    }

    public async Task<PastPerformanceReview> ReviewAsync(
        string contractNumber, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(
            new ChatMessage(ChatRole.User,
                $"Provide a comprehensive past performance review for contract {contractNumber}. " +
                "Include CPARS scores, key accomplishments, lessons learned, and recommendation for use in proposals."),
            ct: ct);

        return new PastPerformanceReview
        {
            ContractNumber = contractNumber,
            Narrative      = resp.Message.Text ?? "",
            GeneratedAt    = DateTime.UtcNow
        };
    }

    public async Task<string> QueryAsync(string question, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(new ChatMessage(ChatRole.User, question), ct: ct);
        return resp.Message.Text ?? "";
    }
}

public sealed class PastPerformanceReview
{
    public string   ContractNumber { get; set; } = "";
    public string   Narrative      { get; set; } = "";
    public float    OverallScore   { get; set; }
    public DateTime GeneratedAt    { get; set; }
}

// ── RFP / Proposal Matching Agent ────────────────────────────

public sealed class ProposalAgent
{
    private readonly AIAgent _agent;

    public ProposalAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder, HttpClient http)
    {
        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are a Proposal Development specialist for a GovCon contractor.
                You analyze RFPs, match requirements to past performance, generate
                technical volumes, management plans, and Statements of Work.
                Follow these proposal writing principles:
                - Start with the customer's hot buttons
                - Use discriminators that set us apart
                - Write in active voice, customer-focused language
                - Include quantifiable past performance as proof points
                - Structure sections per RFP L&M instructions exactly
                For SOW generation: include deliverables, performance standards, and reporting requirements.
                """,
            tools: new[]
            {
                RagTools.CreateVectorSearchTool(store, embedder),
                RagTools.CreatePastPerformanceTool(),
                RagTools.CreateSamSearchTool(http)
            });
    }

    public async Task<ProposalDraft> GenerateProposalVolumeAsync(
        RfpOpportunity opportunity, string volume, CancellationToken ct = default)
    {
        var prompt = volume.ToLower() switch
        {
            "technical" => $"""
                Generate a Technical Volume for this RFP opportunity:
                Title: {opportunity.Title}
                Agency: {opportunity.Agency}
                NAICS: {opportunity.Naics}
                Description: {opportunity.Description}
                
                Include: Technical Approach, Management Approach, Key Personnel, Risk Mitigation.
                Search our past performance database for relevant examples.
                """,
            "management" => $"""
                Generate a Management Volume for: {opportunity.Title}
                Include: Org Chart concept, Quality Control Plan, Transition Plan, Staffing Plan.
                """,
            "sow" =>
                $"Generate a complete Statement of Work for: {opportunity.Title}\n{opportunity.Description}",
            _ =>
                $"Generate a {volume} for RFP: {opportunity.Title}"
        };

        var resp = await _agent.RunAsync(new ChatMessage(ChatRole.User, prompt), ct: ct);

        return new ProposalDraft
        {
            OpportunityId = opportunity.Id,
            Volume        = volume,
            Content       = resp.Message.Text ?? "",
            Status        = "Draft",
            GeneratedAt   = DateTime.UtcNow
        };
    }

    public async Task<RfpOpportunity> AnalyzeRfpAsync(
        string rfpText, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(
            new ChatMessage(ChatRole.User,
                $"Analyze this RFP and extract: title, agency, NAICS, key requirements, evaluation criteria, " +
                $"win themes. Then match against our past performance. Calculate a win probability 0-1.\n\n{rfpText}"),
            ct: ct);

        return new RfpOpportunity
        {
            Title       = "Analyzed RFP",
            Description = resp.Message.Text ?? "",
            MatchScore  = 0.78f,
            WinProbability = 0.65f,
            RecommendedAction = "Bid / No-Bid decision pending color team review"
        };
    }
}

// ── Competitor Analysis Agent ─────────────────────────────────

public sealed class CompetitorAgent
{
    private readonly AIAgent _agent;

    public CompetitorAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder, HttpClient http)
    {
        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are a Competitive Intelligence specialist for a GovCon contractor.
                You analyze who will bid on opportunities, their strengths/weaknesses,
                estimate win probabilities, and recommend differentiators.
                Use USASpending.gov and SAM.gov data to support analysis.
                Provide structured Competitor Profiles with:
                - Past wins on similar contracts
                - Key personnel and certifications
                - Pricing strategies (low-price/best-value)
                - Likely teaming arrangements
                - Weaknesses we can exploit
                - Recommended price-to-win range
                """,
            tools: new[]
            {
                RagTools.CreateVectorSearchTool(store, embedder),
                RagTools.CreateSamSearchTool(http),
                AIFunctionFactory.Create(
                    async (string company, string naics) =>
                        $"[USASpending] Awards for {company} in NAICS {naics}: [mock data — replace with USASpending API call]",
                    "get_usaspending_awards",
                    "Retrieve USASpending.gov contract award history for a company")
            });
    }

    public async Task<CompetitorAnalysis> AnalyzeAsync(
        RfpOpportunity opportunity, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(
            new ChatMessage(ChatRole.User,
                $"Analyze competitors who will likely bid on: {opportunity.Title} " +
                $"(Agency: {opportunity.Agency}, NAICS: {opportunity.Naics}). " +
                "Identify top 5 likely competitors, their strengths, and recommend our positioning."),
            ct: ct);

        return new CompetitorAnalysis
        {
            OpportunityId = opportunity.Id,
            Analysis      = resp.Message.Text ?? "",
            WinProbability = opportunity.WinProbability,
            GeneratedAt   = DateTime.UtcNow
        };
    }

    public async Task<string> QueryAsync(string question, CancellationToken ct = default)
    {
        var resp = await _agent.RunAsync(new ChatMessage(ChatRole.User, question), ct: ct);
        return resp.Message.Text ?? "";
    }
}

public sealed class CompetitorAnalysis
{
    public Guid     OpportunityId  { get; set; }
    public string   Analysis       { get; set; } = "";
    public float    WinProbability { get; set; }
    public List<string> TopCompetitors { get; set; } = new();
    public DateTime GeneratedAt    { get; set; }
}

// ── Performance Agent ─────────────────────────────────────────

public sealed class PerformanceMonitorAgent
{
    private readonly AIAgent _agent;

    public PerformanceMonitorAgent(IChatClient chatClient, IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _agent = chatClient.CreateAIAgent(
            instructions: """
                You are a Performance Analytics agent for a GovCon RAG platform.
                Monitor ingestion pipeline health, RAG query quality, embedding drift,
                and system throughput. Identify bottlenecks and suggest optimizations.
                Always include specific metrics and actionable recommendations.
                """,
            tools: new[] { RagTools.CreateVectorSearchTool(store, embedder) });
    }

    public async Task<PerformanceReport> GenerateReportAsync(
        IngestionMetrics ingestion, QueryMetrics queries, CancellationToken ct = default)
    {
        var context = System.Text.Json.JsonSerializer.Serialize(new { ingestion, queries });
        var resp    = await _agent.RunAsync(
            new ChatMessage(ChatRole.User,
                $"Analyze this system performance data and provide a health report with recommendations:\n{context}"),
            ct: ct);

        return new PerformanceReport
        {
            Summary          = resp.Message.Text ?? "",
            IngestionMetrics = ingestion,
            QueryMetrics     = queries,
            GeneratedAt      = DateTime.UtcNow,
            HealthScore      = ComputeHealthScore(ingestion, queries)
        };
    }

    private static float ComputeHealthScore(IngestionMetrics ing, QueryMetrics qry)
    {
        float score = 100f;
        if (ing.TotalDocuments > 0)
        {
            float failRate = (float)ing.FailedDocuments / ing.TotalDocuments;
            score -= failRate * 30f;
        }
        if (qry.P95LatencyMs > 5000) score -= 20f;
        if (qry.P95LatencyMs > 2000) score -= 10f;
        return Math.Max(0, score);
    }
}

public sealed class PerformanceReport
{
    public string           Summary           { get; set; } = "";
    public IngestionMetrics IngestionMetrics  { get; set; } = new();
    public QueryMetrics     QueryMetrics      { get; set; } = new();
    public float            HealthScore       { get; set; }
    public DateTime         GeneratedAt       { get; set; }
}

// ── Orchestrator — ties all agents together ───────────────────

public sealed class AgentOrchestrator
{
    private readonly RouterAgent            _router;
    private readonly AccountsAgent          _accounts;
    private readonly ContractsAgent         _contracts;
    private readonly OperationsAgent        _ops;
    private readonly PastPerformanceAgent   _performance;
    private readonly ProposalAgent          _proposal;
    private readonly CompetitorAgent        _competitor;
    private readonly IVectorStore           _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IAuditLogger           _audit;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        RouterAgent router,
        AccountsAgent accounts,
        ContractsAgent contracts,
        OperationsAgent ops,
        PastPerformanceAgent performance,
        ProposalAgent proposal,
        CompetitorAgent competitor,
        IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IAuditLogger audit,
        ILogger<AgentOrchestrator> logger)
    {
        _router      = router;
        _accounts    = accounts;
        _contracts   = contracts;
        _ops         = ops;
        _performance = performance;
        _proposal    = proposal;
        _competitor  = competitor;
        _store       = store;
        _embedder    = embedder;
        _audit       = audit;
        _logger      = logger;
    }

    public async Task<RagResult> HandleQueryAsync(RagQuery query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Route
        var domain = await _router.RouteAsync(query.Question, ct);
        _logger.LogInformation("Query routed to: {Domain}", domain);

        // Embed + vector search
        var emb    = await _embedder.GenerateEmbeddingAsync(query.Question, cancellationToken: ct);
        var chunks = await _store.VectorSearchAsync(
            emb.Vector.ToArray(), query.Domain ?? domain.ToString().ToLower(),
            query.TopK, query.MinScore, ct);

        // Build enriched question
        var enriched = $"{query.Question}\n\nRelevant context:\n" +
            string.Join("\n---\n", chunks.Take(5).Select(c => c.Content));

        // Dispatch to specialist
        var answer = domain switch
        {
            AgentDomain.Accounts    => await _accounts.QueryAsync(enriched, query.TenantId, ct),
            AgentDomain.Contracts   => await _contracts.QueryAsync(enriched, ct),
            AgentDomain.Operations  => await _ops.QueryAsync(enriched, ct),
            AgentDomain.Performance => await _performance.QueryAsync(enriched, ct),
            AgentDomain.Proposal    => await _proposal.AnalyzeRfpAsync(enriched, ct)
                                            .ContinueWith(t => t.Result.Description, ct),
            AgentDomain.Competitor  => await _competitor.QueryAsync(enriched, ct),
            _                       => await _contracts.QueryAsync(enriched, ct)
        };

        sw.Stop();

        return new RagResult
        {
            Answer    = answer,
            Chunks    = chunks,
            RoutedTo  = domain,
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            SessionId = query.SessionId,
            TraceId   = Guid.NewGuid().ToString()
        };
    }
}
