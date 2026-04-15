using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using System.Text;

namespace SharePointRag.PastPerformance;

/// <summary>
/// Microsoft.Agents.AI bot handler for the GovCon Past Performance Agent.
///
/// Supported channels: Microsoft Teams, WebChat, Direct Line, Outlook.
///
/// Intent routing (handled automatically by the orchestrator):
///   • "Show me IT modernisation contracts similar to this SOW"
///     → FindSimilarContracts: ranked contract list with relevance rationale
///
///   • "Draft the past performance volume for solicitation W912DQ-24-R-0041"
///     → GenerateVolumeSection: full narrative drafts + executive summary
///
///   • "Who is our CO reference for our DHS work?"
///     → FindReferences: formatted CO/COR contact blocks
///
///   • "What are our CPARS ratings for HHS contracts?"
///     → ExtractCPARSRatings: markdown table of ratings
///
///   • "Do we have NAICS 541512 experience in the last 3 years?"
///     → IdentifyGaps: gap analysis with risk ratings
///
///   • "Who has led cloud migration programmes for DoD?"
///     → FindKeyPersonnel: personnel roster with relevant experience
///
///   • "Summarise our DoD portfolio for an upcoming IDIQ re-compete"
///     → SummarisePortfolio: executive portfolio summary
///
/// Commands: /help  /intents  /status
/// </summary>
public sealed class PastPerformanceAgent : AgentApplication
{
    private readonly IPastPerformanceOrchestrator _orchestrator;
    private readonly ILogger<PastPerformanceAgent> _logger;

    public PastPerformanceAgent(
        AgentApplicationOptions options,
        IPastPerformanceOrchestrator orchestrator,
        ILogger<PastPerformanceAgent> logger)
        : base(options)
    {
        _orchestrator = orchestrator;
        _logger       = logger;

        OnActivity(ActivityTypes.Message,           OnMessageAsync);
        OnActivity(ActivityTypes.ConversationUpdate, OnConversationUpdateAsync);

        OnMessage("/help",    OnHelpAsync);
        OnMessage("/intents", OnIntentsAsync);
        OnMessage("/status",  OnStatusAsync);
    }

    // ── Welcome ───────────────────────────────────────────────────────────────

    private async Task OnConversationUpdateAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        if (ctx.Activity.MembersAdded?.Any(m => m.Id != ctx.Activity.Recipient.Id) == true)
        {
            await ctx.SendActivityAsync(
                """
                🏛️ **Welcome to the Past Performance Agent**

                I help GovCon capture and proposal teams find, analyse, and draft
                past performance content from your SharePoint document library.

                **What I can do:**
                • Find contracts relevant to a new solicitation or SOW
                • Draft a complete Past Performance Volume (narratives + executive summary)
                • Look up CO/COR reference contacts
                • Extract and summarise CPARS ratings
                • Identify gaps vs. RFP requirements
                • Find key personnel with relevant experience

                **Try asking:**
                > "Find IT modernisation contracts similar to a FISMA compliance SOW"
                > "Draft a past performance volume for solicitation HHS-24-001"
                > "What are our CPARS ratings for DoD work?"

                Type **/help** for more guidance or **/intents** to see all query types.
                """, ct);
        }
    }

    // ── Main message handler ──────────────────────────────────────────────────

    private async Task OnMessageAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var message = ctx.Activity.Text?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        await ctx.SendActivityAsync(Activity.CreateTypingActivity(), ct);

        try
        {
            var response = await _orchestrator.HandleAsync(message, ct);
            var reply    = FormatResponse(response);
            await ctx.SendActivityAsync(reply, ct);

            // If a full volume was drafted, send a follow-up with download hint
            if (response.DraftedSection is not null)
            {
                await ctx.SendActivityAsync(
                    "📄 The full volume with formatted narratives is available at:\n" +
                    "`POST /api/pastperformance/volume` — see API docs for download options.", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PastPerformance pipeline error for: {M}", message);
            await ctx.SendActivityAsync(
                "⚠️ I encountered an error processing your request. " +
                "Please check that your past performance documents are indexed and try again.", ct);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task OnHelpAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            """
            ## 🏛️ Past Performance Agent — Help

            ### Commands
            | Command | Description |
            |---|---|
            | `/help` | This help message |
            | `/intents` | Show all supported query intents with examples |
            | `/status` | Show index and service status |

            ### Tips
            - Be specific about agency, NAICS, dollar thresholds, and time periods.
            - Include solicitation numbers when requesting volume drafts.
            - Reference relevant SOW sections for better relevance matching.
            - The agent reads from your SharePoint library — ensure documents are indexed.

            ### Data sources expected in your library
            - Past Performance Questionnaires (PPQs)
            - CPARS printouts / screenshots
            - Contract award documents (SF-26, SF-1449)
            - Performance Work Statements (for scope context)
            - Lessons Learned reports
            - Proposal past performance volumes (prior submissions)
            """, ct);
    }

    private async Task OnIntentsAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            """
            ## 🎯 Supported Query Intents

            | Intent | Example Query |
            |---|---|
            | **Find Similar Contracts** | "Show me cloud infrastructure contracts similar to this JWCC task order" |
            | **Generate Volume Section** | "Draft a past performance volume for solicitation W912DQ-24-R-0041" |
            | **Find References** | "Who is our CO reference for our CMS work?" |
            | **Summarise Portfolio** | "Give me an executive summary of our DHS past performance" |
            | **Identify Gaps** | "Do we have NAICS 541512 work completed in the last 5 years?" |
            | **Extract CPARS Ratings** | "What are our CPARS ratings across all federal health IT contracts?" |
            | **Find Key Personnel** | "Which of our staff have led DevSecOps programmes for DoD?" |
            | **General** | Any freeform question about your past performance portfolio |
            """, ct);
    }

    private async Task OnStatusAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            "📊 **Status:** Past Performance Agent is operational.\n" +
            "Vector index: SharpCoreDB HNSW | LLM: Azure OpenAI GPT-4o\n" +
            "Use `/help` for usage guidance.", ct);
    }

    // ── Response formatting ───────────────────────────────────────────────────

    private static string FormatResponse(PastPerformanceResponse response)
    {
        var sb = new StringBuilder();

        // Main answer (already markdown formatted by orchestrator)
        sb.AppendLine(response.Answer);

        // Relevant contracts summary card (if not already in the answer)
        if (response.RelevantContracts.Count > 0
            && response.Query.Intent is not QueryIntent.GenerateVolumeSection
                                     and not QueryIntent.SummarisePortfolio
                                     and not QueryIntent.FindReferences
                                     and not QueryIntent.ExtractCPARSRatings
                                     and not QueryIntent.FindKeyPersonnel)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("**Relevant Contracts Retrieved**");
            foreach (var c in response.RelevantContracts.Take(5))
            {
                var value = (c.FinalObligatedValue ?? c.ContractValue) is decimal v
                    ? $"${v:N0}"
                    : "Value N/A";
                var period = c.IsOngoing
                    ? $"{c.StartDate} – Ongoing"
                    : $"{c.StartDate} – {c.EndDate}";
                var cpars = string.IsNullOrEmpty(c.CPARSRatingOverall)
                    ? string.Empty
                    : $" | CPARS: **{c.CPARSRatingOverall}**";

                sb.AppendLine($"- **{c.ContractNumber}** — {c.AgencyAcronym ?? c.AgencyName} | {c.Title}");
                sb.AppendLine($"  {value} | {period}{cpars}");
                if (!string.IsNullOrEmpty(c.SourceDocumentUrl))
                    sb.AppendLine($"  📄 [{c.SourceFileName}]({c.SourceDocumentUrl})");
            }
        }

        // Warnings
        if (response.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("⚠️ **Attention Required**");
            foreach (var w in response.Warnings)
                sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }
}
