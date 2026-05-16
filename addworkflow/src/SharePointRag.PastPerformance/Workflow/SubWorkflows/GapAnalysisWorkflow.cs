using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using SharePointRag.PastPerformance.Workflow.Tools;

namespace SharePointRag.PastPerformance.Workflow.SubWorkflows;

/// <summary>
/// Sub-workflow: Past Performance Gap Analysis
///
/// Intent: IdentifyGaps
/// Sequential: RequirementParser → PortfolioSearcher → GapAnalyser
///
///   RequirementParser — extracts evaluation factors and PP requirements
///                       from the solicitation text
///
///   PortfolioSearcher — uses RAG and data source tools to search the portfolio
///                       against each requirement
///
///   GapAnalyser — compares requirements vs portfolio, rates risk, suggests
///                 mitigations; uses plugin tools for market intelligence
/// </summary>
public static class GapAnalysisWorkflow
{
    public static Workflow Build(
        IChatClient               chatClient,
        IReadOnlyList<AIFunction> ragAndSourceTools,
        IReadOnlyList<AIFunction> pluginTools)
    {
        // ── Step 1: Requirement Parser ────────────────────────────────────────
        AIAgent requirementParser = chatClient.AsAIAgent(
            instructions:
                """
                You are a GovCon capture analyst. Extract all past performance requirements
                from the provided solicitation text.

                For each requirement identify:
                - Required NAICS code(s)
                - Minimum contract value threshold
                - Required agency types (Federal/DoD/Civilian/SLED)
                - Required contract types (FFP, CPFF, T&M, IDIQ)
                - Technology domains or keywords
                - Recency requirement (e.g. "within 5 years")
                - CPARS rating minimums (if specified)
                - Team composition requirements (prime only, teaming allowed)

                Output a structured list of requirements. Be precise.
                """,
            name: "RequirementParser");

        // ── Step 2: Portfolio Searcher ────────────────────────────────────────
        AIAgent portfolioSearcher = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "PortfolioSearcher",
            Instructions =
                """
                You are a GovCon database analyst. You receive a list of past performance
                requirements. For EACH requirement, search the portfolio using the available
                tools and determine whether we have qualifying contracts.

                Search strategy per requirement:
                - Use search_past_performance for broad discovery
                - Use source-specific tools (search_deltek*, search_sql*, etc.) for structured data
                - Filter by recency, dollar value, agency type, NAICS code as needed

                For each requirement, report:
                ✅ COVERED — contract(s) found that qualify
                ⚠️ PARTIAL — some evidence but may not fully satisfy
                ❌ MISSING — no qualifying contract found

                Be conservative — if you're unsure, mark as PARTIAL.
                """,
            ChatOptions  = new ChatOptions { Tools = ragAndSourceTools }
        });

        // ── Step 3: Gap Analyser ──────────────────────────────────────────────
        AIAgent gapAnalyser = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "GapAnalyser",
            Instructions =
                """
                You are a GovCon capture strategist. You receive the portfolio coverage
                assessment from the PortfolioSearcher.

                For each gap (MISSING or PARTIAL), provide:
                1. Gap description (what requirement is not met)
                2. Risk level: High / Medium / Low
                3. Mitigation options:
                   - Teaming: find a partner who has this experience
                   - Subcontracting: bring in a sub with relevant work
                   - Reference expansion: include related work types
                   - Waiver request: if the agency allows alternatives

                You may call external AI tools (plugin tools) to get market intelligence:
                - Which firms might have the missing experience?
                - Are there similar contracts in SAM.gov that could qualify?

                End with an EXECUTIVE RISK SUMMARY: overall bid viability (Strong/Moderate/Risky)
                and the single most critical gap to address.
                """,
            ChatOptions  = new ChatOptions { Tools = pluginTools }
        });

        return new WorkflowBuilder(requirementParser)
            .WithName("gap-analysis")
            .WithDescription("Analyses past performance gaps vs solicitation requirements")
            .AddEdge(requirementParser, portfolioSearcher)
            .AddEdge(portfolioSearcher, gapAnalyser)
            .Build();
    }
}
