using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace SharePointRag.PastPerformance.Workflow.SubWorkflows;

/// <summary>
/// Sub-workflow: CPARS Ratings Extraction
///
/// Intent: ExtractCPARSRatings
/// Sequential: CPARSSearcher → CPARSFormatter
///
///   CPARSSearcher  — queries all data sources for CPARS rating data;
///                    structured sources (Deltek, SQL, Excel) are preferred
///                    since they have discrete rating fields
///
///   CPARSFormatter — builds a comparative markdown table and flags contracts
///                    with missing ratings
/// </summary>
public static class CPARSRatingsWorkflow
{
    public static Workflow Build(
        IChatClient               chatClient,
        IReadOnlyList<AIFunction> ragAndSourceTools,
        IReadOnlyList<AIFunction> pluginTools)
    {
        // ── Step 1: CPARS Searcher ────────────────────────────────────────────
        AIAgent cparsSearcher = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "CPARSSearcher",
            Instructions =
                """
                You are a GovCon performance analyst. Search for CPARS ratings data across
                all available data sources using the search tools.

                Priority sources for CPARS data (search these first):
                - Any source with "CPARS" in the name (CPARSExport, ExternalCPARSDatabase)
                - Deltek sources (ProjectNumber, CPARSRating fields)
                - SQL database sources (CPARS_OVERALL, CPARS_QUALITY columns)

                For each contract found, collect all rating dimensions:
                  - Overall rating
                  - Quality of product/service
                  - Schedule/timeliness
                  - Cost control
                  - Management/business relations
                  - Small business subcontracting (if applicable)

                Also collect: contract number, agency, period, dollar value, contracting officer.
                If a structured source has the rating in metadata fields, use that directly.
                For document sources, extract ratings from narrative text.

                Apply filters from the user's request (agency name, time period, etc.)
                """,
            ChatOptions  = new ChatOptions { Tools = ragAndSourceTools.Concat(pluginTools).ToList() }
        });

        // ── Step 2: CPARS Formatter ───────────────────────────────────────────
        AIAgent cparsFormatter = chatClient.AsAIAgent(
            instructions:
                """
                You receive raw CPARS data from the CPARSSearcher. Format it as:

                1. A markdown table with columns:
                   Contract | Agency | Value | Period | Source | Overall | Quality | Schedule | Cost | Mgmt

                2. A SUMMARY section:
                   - Average rating per dimension (where data is available)
                   - Highest and lowest rated contracts
                   - Count of Exceptional / Very Good / Satisfactory / Below ratings

                3. A DATA GAPS section listing contracts where ratings are missing,
                   with a note to retrieve from cpars.gov or the contracts database.

                Use these abbreviations in the table: E=Exceptional, VG=Very Good,
                S=Satisfactory, M=Marginal, U=Unsatisfactory, —=Not Available

                End with: *Ratings sourced from: [data source names]*
                """,
            name: "CPARSFormatter");

        return new WorkflowBuilder(cparsSearcher)
            .WithName("cpars-ratings")
            .WithDescription("Extracts and formats CPARS ratings from all data sources")
            .AddEdge(cparsSearcher, cparsFormatter)
            .Build();
    }
}
