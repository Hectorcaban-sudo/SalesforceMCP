using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace SharePointRag.PastPerformance.Workflow.SubWorkflows;

/// <summary>
/// Sub-workflow: Contract Search and References
///
/// Intents: FindSimilarContracts, FindReferences, SummarisePortfolio, General
/// Sequential: SearchAgent → ResponseAgent
///
///   SearchAgent  — searches all data sources using every available tool;
///                  this is the "catch-all" workflow for intents that don't
///                  need specialised processing
///
///   ResponseAgent — formats results as a clear, actionable list with citations
/// </summary>
public static class ContractSearchWorkflow
{
    public static Workflow Build(
        IChatClient               chatClient,
        IReadOnlyList<AIFunction> ragAndSourceTools,
        IReadOnlyList<AIFunction> pluginTools)
    {
        // ── Step 1: Search Agent ──────────────────────────────────────────────
        AIAgent searchAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "ContractSearcher",
            Instructions =
                """
                You are a GovCon past performance specialist. Search for relevant contracts
                that match the user's query using ALL available tools.

                Search strategy:
                1. Start with search_past_performance for broad discovery
                2. If the user specifies a data source, use that source's specific tool
                3. Use plugin tools to enrich results with external data if useful
                4. If searching for references, collect CO/COR contact information

                For portfolio summaries:
                - Search broadly across all sources
                - Group contracts by agency type, NAICS code, dollar tier
                - Compute totals (total value, count, recency distribution)

                For reference searches:
                - Find contracts with complete CO/COR contact information
                - Prioritise recent contracts (within 3 years)

                Return raw findings — the ResponseAgent will format them.
                """,
            ChatOptions  = new ChatOptions
            {
                Tools = ragAndSourceTools.Concat(pluginTools).ToList()
            }
        });

        // ── Step 2: Response Agent ────────────────────────────────────────────
        AIAgent responseAgent = chatClient.AsAIAgent(
            instructions:
                """
                You receive contract search results and format them for a capture manager.

                For FIND_SIMILAR_CONTRACTS:
                Present top contracts as a ranked list:
                - Contract number | Agency | Title | Value | Period | CPARS | Source

                For FIND_REFERENCES:
                Present a reference block per contract:
                  CO: [Name] | [Phone] | [Email]
                  COR: [Name] | [Phone] | [Email]
                  Contract: [Number] | Agency: [Name] | Value: $[Amount]

                For SUMMARISE_PORTFOLIO:
                Present an executive summary with:
                - Total contract value and count
                - Top agencies by contract count
                - NAICS distribution
                - Recency analysis (active, <3yr, 3-5yr, >5yr)
                - CPARS rating distribution

                For GENERAL queries:
                Answer the question directly using the found data, with citations.

                Always end with: *Sources searched: [list of data sources]*
                """,
            name: "ContractResponder");

        return new WorkflowBuilder(searchAgent)
            .WithName("contract-search")
            .WithDescription("Searches contracts and formats results for the capture team")
            .AddEdge(searchAgent, responseAgent)
            .Build();
    }
}
