using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Interfaces;
using SharePointRag.PastPerformance.Extensions;
using SharePointRag.PastPerformance.Plugins;
using SharePointRag.PastPerformance.Workflow.SubWorkflows;
using SharePointRag.PastPerformance.Workflow.Tools;

namespace SharePointRag.PastPerformance.Workflow;

/// <summary>
/// Top-level Past Performance Workflow factory.
///
/// Architecture:
///
///   User question
///       │
///       ▼ RouterAgent (ChatClientAgent, all tools registered)
///         │
///         ├─ tool: draft_volume(solicitation_context)
///         │     → VolumeDraftWorkflow (ContractFinder → NarrativeDrafter → VolumeAssembler)
///         │
///         ├─ tool: analyse_gaps(requirements)
///         │     → GapAnalysisWorkflow (RequirementParser → PortfolioSearcher → GapAnalyser)
///         │
///         ├─ tool: get_cpars_ratings(query)
///         │     → CPARSRatingsWorkflow (CPARSSearcher → CPARSFormatter)
///         │
///         ├─ tool: find_key_personnel(query)
///         │     → KeyPersonnelWorkflow (PersonnelSearcher → PersonnelResponder)
///         │
///         ├─ tool: search_contracts(query)
///         │     → ContractSearchWorkflow (ContractSearcher → ContractResponder)
///         │
///         ├─ tool: search_past_performance(query)   — direct RAG (cross-system)
///         ├─ tool: search_{dataSourceName}(query)   — per-source vector search
///         └─ tool: call_{pluginName}(question)      — external AI plugin
///
/// Sub-workflows are proper MAF Workflow objects converted to AIAgent via AsAIAgent().
/// They are then wrapped as AIFunction tools so the RouterAgent can call them via
/// GPT-4o tool-calling, routing by intent.
///
/// Shared tools (RAG search, data source searches, plugin calls) are available to:
///   - RouterAgent (for quick lookups without invoking a sub-workflow)
///   - Every agent inside every sub-workflow (each agent gets a copy of the tools)
///
/// Endpoints:
///   POST /api/pastperformance/workflow/run    — full response
///   POST /api/pastperformance/workflow/stream — SSE streaming
/// </summary>
public static class PastPerformanceWorkflowFactory
{
    /// <summary>
    /// Builds the top-level workflow.
    /// Called from Program.cs: builder.AddWorkflow("pp-workflow", Build).AddAsAIAgent()
    /// </summary>
    public static Workflow Build(IServiceProvider sp, string workflowName)
    {
        var chatClient  = sp.GetRequiredService<IChatClient>();
        var registry    = sp.GetRequiredService<ILibraryRegistry>();
        var embedder    = sp.GetRequiredService<IEmbeddingService>();
        var agentOpts   = sp.GetRequiredService<IOptions<PastPerformanceAgentOptions>>().Value;
        var plugins     = sp.GetServices<IExternalAiPlugin>().ToList();

        // ── 1. Build shared tools ─────────────────────────────────────────────
        // These are available to the router AND to every agent inside sub-workflows.

        var ragOrchFactory = sp.GetRequiredService<IRagOrchestratorFactory>();
        var ragOrchestrator = ragOrchFactory.Create(
            agentOpts.SystemNames.Count > 0
                ? agentOpts.SystemNames
                : ["PastPerformance"]);

        var ragSearchTool    = PPToolFactory.CreateRagSearchTool(ragOrchestrator);
        var dataSourceTools  = PPToolFactory.CreateDataSourceTools(
            registry, embedder, agentOpts.SystemNames);
        var pluginTools      = PPToolFactory.CreatePluginTools(plugins);

        // Shared tools = RAG + all data sources (available to router and sub-workflow agents)
        var sharedTools = new List<AIFunction> { ragSearchTool }
            .Concat(dataSourceTools)
            .ToList();

        // ── 2. Build sub-workflows and wrap as AIFunction tools ───────────────

        // Volume Draft — 3-step sequential
        var volumeWorkflow = VolumeDraftWorkflow
            .Build(chatClient, sharedTools, pluginTools)
            .AsAIAgent(id: "volume-draft", name: "VolumeDraftWorkflow",
                description: "Drafts complete Past Performance Volume sections");

        var draftVolumeTool = PPToolFactory.CreateSubWorkflowTool(
            toolName:    "draft_volume_section",
            description: "Drafts a complete GovCon Past Performance Volume section including " +
                         "contract narratives, CPARS ratings, reference blocks, and gap analysis. " +
                         "Call this when the user wants a volume draft for a specific solicitation. " +
                         "Pass the full solicitation context or SOW description.",
            subWorkflowAgent: volumeWorkflow);

        // Gap Analysis — 3-step sequential
        var gapWorkflow = GapAnalysisWorkflow
            .Build(chatClient, sharedTools, pluginTools)
            .AsAIAgent(id: "gap-analysis", name: "GapAnalysisWorkflow",
                description: "Analyses past performance gaps vs solicitation requirements");

        var analyseGapsTool = PPToolFactory.CreateSubWorkflowTool(
            toolName:    "analyse_gaps",
            description: "Analyses the company's past performance portfolio against solicitation " +
                         "requirements to identify gaps, rate risk, and suggest mitigations. " +
                         "Call this when the user wants a gap analysis or bid readiness assessment. " +
                         "Pass the solicitation requirements or evaluation criteria.",
            subWorkflowAgent: gapWorkflow);

        // CPARS Ratings — 2-step sequential
        var cparsWorkflow = CPARSRatingsWorkflow
            .Build(chatClient, sharedTools, pluginTools)
            .AsAIAgent(id: "cpars-ratings", name: "CPARSRatingsWorkflow",
                description: "Extracts and formats CPARS ratings from all data sources");

        var cparsRatingsTool = PPToolFactory.CreateSubWorkflowTool(
            toolName:    "get_cpars_ratings",
            description: "Extracts CPARS performance ratings across all indexed data sources " +
                         "(Deltek, SQL databases, Excel exports, CPARS.gov exports, SharePoint docs). " +
                         "Call this when the user wants a ratings table or CPARS comparison. " +
                         "Optionally filter by agency, time period, or contract type.",
            subWorkflowAgent: cparsWorkflow);

        // Key Personnel — 2-step sequential
        var personnelWorkflow = KeyPersonnelWorkflow
            .Build(chatClient, sharedTools, pluginTools)
            .AsAIAgent(id: "key-personnel", name: "KeyPersonnelWorkflow",
                description: "Finds key personnel with relevant experience");

        var keyPersonnelTool = PPToolFactory.CreateSubWorkflowTool(
            toolName:    "find_key_personnel",
            description: "Finds key personnel with relevant domain experience by searching " +
                         "Deltek Employees, SQL personnel tables, and SharePoint resumes. " +
                         "Returns a roster with names, titles, clearances, and relevant contracts. " +
                         "Call this when the user needs to staff a position or find qualified personnel.",
            subWorkflowAgent: personnelWorkflow);

        // Contract Search — 2-step sequential (general / references / portfolio)
        var searchWorkflow = ContractSearchWorkflow
            .Build(chatClient, sharedTools, pluginTools)
            .AsAIAgent(id: "contract-search", name: "ContractSearchWorkflow",
                description: "Searches contracts and formats results");

        var searchContractsTool = PPToolFactory.CreateSubWorkflowTool(
            toolName:    "search_contracts",
            description: "Searches all past performance records across every data source for " +
                         "contracts matching the query. Use for: finding similar contracts, " +
                         "getting CO/COR references, summarising the portfolio, or answering " +
                         "general past performance questions. This is the default search tool.",
            subWorkflowAgent: searchWorkflow);

        // ── 3. Build plugin tools as direct router tools ──────────────────────
        // Plugins are available both directly on the router (for quick lookups)
        // and inside sub-workflows. The router can call them without invoking a
        // full sub-workflow if the question is narrow enough.
        var directPluginTools = PPToolFactory.CreatePluginTools(plugins);

        // ── 4. Build the RouterAgent with ALL tools ───────────────────────────
        var allRouterTools = new List<AIFunction>
        {
            // Sub-workflow dispatchers (primary routing tools)
            draftVolumeTool,
            analyseGapsTool,
            cparsRatingsTool,
            keyPersonnelTool,
            searchContractsTool,

            // Direct tools for quick inline lookups without a sub-workflow
            ragSearchTool
        }
        .Concat(dataSourceTools)     // search_{dataSource} for each source
        .Concat(directPluginTools)   // call_{plugin} for each external AI
        .ToList();

        AIAgent routerAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "PPRouter",
            Instructions = BuildRouterInstructions(
                agentOpts.SystemNames,
                dataSourceTools.Select(t => t.Name).ToList(),
                directPluginTools.Select(t => t.Name).ToList()),
            ChatOptions  = new ChatOptions { Tools = allRouterTools }
        });

        // ── 5. Build the top-level workflow ───────────────────────────────────
        // The router is a single-node workflow. Its tool-calling behaviour IS
        // the workflow: it dispatches to sub-workflows as needed and synthesises
        // the final response from tool outputs.
        return new WorkflowBuilder(routerAgent)
            .WithName(workflowName)
            .WithDescription(
                "GovCon Past Performance expert: routes by intent to specialised " +
                "sub-workflows (volume drafts, gap analysis, CPARS ratings, key " +
                "personnel, contract search) and uses data sources + external AI " +
                "plugins as tools for comprehensive research.")
            .Build();
    }

    // ── Router system prompt ──────────────────────────────────────────────────

    private static string BuildRouterInstructions(
        IReadOnlyList<string> systemNames,
        IReadOnlyList<string> dataSourceToolNames,
        IReadOnlyList<string> pluginToolNames)
    {
        var sourceList = dataSourceToolNames.Count > 0
            ? string.Join(", ", dataSourceToolNames)
            : "search_past_performance";

        var pluginList = pluginToolNames.Count > 0
            ? string.Join(", ", pluginToolNames)
            : "none configured";

        return $"""
            You are a GovCon Past Performance expert AI. You route user requests to
            specialised sub-workflows and use data source + plugin tools to answer questions.

            AVAILABLE SUB-WORKFLOWS (call these for complex, multi-step work):
            - draft_volume_section    — full proposal volume with narratives, ratings, references
            - analyse_gaps            — gap analysis vs solicitation requirements
            - get_cpars_ratings       — extract and compare performance ratings
            - find_key_personnel      — find qualified staff for a role or position
            - search_contracts        — general contract search, references, portfolio summary

            AVAILABLE DATA SOURCE TOOLS (use for targeted, single-source lookups):
            {sourceList}

            AVAILABLE EXTERNAL AI TOOLS (use for market intel not in the knowledge base):
            {pluginList}

            ROUTING RULES:
            - Volume draft / proposal writing   → always call draft_volume_section
            - Gap analysis / bid readiness      → always call analyse_gaps
            - CPARS / performance ratings       → always call get_cpars_ratings
            - Personnel / staffing / resumes    → always call find_key_personnel
            - Quick lookups / references        → use search_contracts or a direct source tool
            - External data / market intel      → use the appropriate plugin tool first,
                                                  then synthesise with internal data

            You may call MULTIPLE tools in one response if the question requires it.
            For example: "Draft a volume AND show CPARS ratings" → call draft_volume_section
            AND get_cpars_ratings in parallel.

            ALWAYS include in your final response:
            - Which sub-workflow(s) or tool(s) you used
            - Which data sources contributed to the answer
            - Any data gaps that warrant attention

            RAG Systems available: {string.Join(", ", systemNames)}
            """;
    }
}
