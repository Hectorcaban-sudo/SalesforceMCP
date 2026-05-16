using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace SharePointRag.PastPerformance.Workflow.SubWorkflows;

/// <summary>
/// Sub-workflow: Key Personnel Discovery
///
/// Intent: FindKeyPersonnel
/// Sequential: PersonnelSearcher → PersonnelResponseAgent
///
///   PersonnelSearcher — searches all sources for personnel with relevant
///                       experience; Deltek Employees endpoint and SQL
///                       databases with personnel tables are primary sources
///
///   PersonnelResponseAgent — formats a personnel roster with names, titles,
///                             clearances, relevant contracts, and roles
/// </summary>
public static class KeyPersonnelWorkflow
{
    public static Workflow Build(
        IChatClient               chatClient,
        IReadOnlyList<AIFunction> ragAndSourceTools,
        IReadOnlyList<AIFunction> pluginTools)
    {
        // ── Step 1: Personnel Searcher ────────────────────────────────────────
        AIAgent personnelSearcher = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name         = "PersonnelSearcher",
            Instructions =
                """
                You are a GovCon HR and staffing analyst. Find key personnel with
                relevant experience for the role or domain described by the user.

                Priority sources for personnel data:
                - Deltek sources (Employees endpoint — use search_deltek* tools)
                - SQL databases with employee/personnel tables
                - SharePoint records (resumes, org charts, prior proposal bios)

                For each person found, collect:
                - Full name and title/role
                - Security clearance level (if available)
                - Years of experience in the relevant domain
                - Relevant contracts they supported (contract numbers, agencies)
                - Specific role on each contract (PM, Tech Lead, Subject Matter Expert)
                - Education and certifications (if available)
                - Availability status (if in structured sources)

                Eliminate duplicates — merge records for the same person from different sources.
                Return the top 10 most relevant personnel.
                """,
            ChatOptions  = new ChatOptions
            {
                Tools = ragAndSourceTools.Concat(pluginTools).ToList()
            }
        });

        // ── Step 2: Personnel Response Agent ─────────────────────────────────
        AIAgent personnelResponder = chatClient.AsAIAgent(
            instructions:
                """
                You receive personnel data from the PersonnelSearcher.

                Format the output as:

                ## Key Personnel with Relevant Experience

                For each person:
                **[Name]** — [Title]
                - Clearance: [level or "Not specified"]
                - Experience: [domain, years]
                - Relevant contracts: [list with agency and role]
                - Certifications: [if available]

                End with a STAFFING SUMMARY:
                - Total personnel found: N
                - Cleared personnel: N
                - Primary sources: [list]
                - Any notable gaps (e.g. "No cleared personnel found for this role")

                If personnel data was sparse, suggest which data sources should be
                checked or indexed (e.g. "Deltek Employees endpoint not indexed").
                """,
            name: "PersonnelResponder");

        return new WorkflowBuilder(personnelSearcher)
            .WithName("key-personnel")
            .WithDescription("Finds and formats key personnel with relevant experience")
            .AddEdge(personnelSearcher, personnelResponder)
            .Build();
    }
}
