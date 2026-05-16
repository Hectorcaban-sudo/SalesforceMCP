using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using SharePointRag.PastPerformance.Workflow.Tools;

namespace SharePointRag.PastPerformance.Workflow.SubWorkflows;

/// <summary>
/// Sub-workflow: Past Performance Volume Draft
///
/// Intent: GenerateVolumeSection
/// Sequential: ContractFinder → NarrativeDrafter → VolumeAssembler
///
///   ContractFinder  — uses all RAG + data source + plugin tools to retrieve
///                     the most relevant contracts for the solicitation
///
///   NarrativeDrafter — uses RAG and plugin tools to draft FAR 15.305(a)(2)
///                      narratives for each found contract; each narrative is
///                      ~500 words with CPARS ratings, accomplishments, references
///
///   VolumeAssembler — assembles the final volume: executive summary, all
///                     narratives, reference blocks, and flagged gaps
/// </summary>
public static class VolumeDraftWorkflow
{
    public static Workflow Build(
        IChatClient             chatClient,
        IReadOnlyList<AIFunction> ragAndSourceTools,
        IReadOnlyList<AIFunction> pluginTools)
    {
        var allTools = ragAndSourceTools.Concat(pluginTools).ToList();

        // ── Step 1: Contract Finder ───────────────────────────────────────────
        // Has full access to all data sources and plugins to find relevant work
        AIAgent contractFinder = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "ContractFinder",
            Instructions =
                """
                You are a GovCon past performance specialist. Your job is to find the most
                relevant contracts for a given solicitation or SOW.

                Use the search tools to query ALL available data sources (SharePoint, SQL,
                Deltek, Excel, and any connected external AI systems). Cast a wide net —
                search by NAICS code, agency name, technology keywords, and contract type.

                For each contract found, collect:
                - Contract number, agency, period of performance, dollar value
                - CPARS ratings (overall, quality, schedule, cost control, management)
                - Key accomplishments and measurable outcomes
                - Contracting Officer name, phone, and email
                - Subcontractors and teaming arrangements

                Return the top 5-7 most relevant contracts with full details.
                Be specific — capture managers need exact data, not summaries.
                """,
            ChatOptions  = new ChatOptions { Tools = allTools }
        });

        // ── Step 2: Narrative Drafter ─────────────────────────────────────────
        // Receives contract list from step 1, drafts individual narratives
        AIAgent narrativeDrafter = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "NarrativeDrafter",
            Instructions =
                """
                You are a senior GovCon proposal writer specialising in Past Performance volumes.
                You receive a list of relevant contracts from the ContractFinder.

                For EACH contract, draft a ~500-word narrative that:
                1. Opens with a relevance statement linking scope to the solicitation
                2. States contract number, agency, PoP dates, and total value
                3. Includes CPARS ratings if available (required by most RFPs)
                4. Highlights ≥3 specific, measurable accomplishments
                5. Notes schedule adherence, cost control, and quality performance
                6. Lists Contracting Officer reference (name, phone, email)
                7. Flags any missing data with [VERIFY]

                You may use the search tools to pull additional detail on specific contracts.
                Use plugin tools if you need external market context.

                Write in third person. Do NOT fabricate data — only use what is provided.
                """,
            ChatOptions  = new ChatOptions { Tools = allTools }
        });

        // ── Step 3: Volume Assembler ──────────────────────────────────────────
        // Assembles the final deliverable — no search tools needed
        AIAgent volumeAssembler = chatClient.AsAIAgent(
            instructions:
                """
                You are a GovCon proposal manager. You receive all drafted narratives.

                Assemble the complete Past Performance Volume:
                1. EXECUTIVE SUMMARY (3-5 sentences, ≤150 words): total relevant experience,
                   strongest CPARS ratings, data source breadth, team qualifications
                2. CONTRACT NARRATIVES: all narratives in order of relevance score
                3. REFERENCE BLOCKS: formatted CO/COR contacts after each narrative
                4. GAP ANALYSIS: flag any missing CPARS data, expired references, or
                   thin coverage areas (agency types, NAICS codes, dollar thresholds)
                5. SOURCES SEARCHED: list which data sources contributed records

                Format the entire volume in clean markdown.
                End with: *Generated from [N] data source(s): [source names]*
                """,
            name: "VolumeAssembler");

        return new WorkflowBuilder(contractFinder)
            .WithName("volume-draft")
            .WithDescription("Drafts a GovCon Past Performance Volume section")
            .AddEdge(contractFinder, narrativeDrafter)
            .AddEdge(narrativeDrafter, volumeAssembler)
            .Build();
    }
}
