using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using System.Text;

namespace SharePointRag.PastPerformance;

/// <summary>
/// Microsoft.Agents.AI Past Performance bot.
/// Searches all configured data sources — SharePoint, SQL, Excel, Deltek, Custom —
/// whatever systems are listed in PastPerformanceAgent.SystemNames.
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
        OnMessage("/sources", OnSourcesAsync);
        OnMessage("/status",  OnStatusAsync);
    }

    private async Task OnConversationUpdateAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        if (ctx.Activity.MembersAdded?.Any(m => m.Id != ctx.Activity.Recipient.Id) == true)
        {
            await ctx.SendActivityAsync(
                """
                🏛️ **Welcome to the Past Performance Agent**

                I search across **all configured data sources** to find, analyse,
                and draft past performance content:
                • SharePoint document libraries (PPQs, CPARS printouts, proposal volumes)
                • SQL databases (Deltek Costpoint, custom contract DBs)
                • Deltek Vantagepoint (Projects, Clients, Employees, Opportunities)
                • Excel / CSV files (contract matrices, skills spreadsheets)
                • Any custom REST API connector

                **Try asking:**
                > "Find IT modernisation contracts similar to this FISMA SOW"
                > "Draft a past performance volume for solicitation HHS-24-001"
                > "What CPARS ratings do we have for DoD work in Deltek?"
                > "Find contracts from our SQL database similar to cloud migration"

                Type **/help** for guidance · **/sources** to see indexed data sources
                """, ct);
        }
    }

    private async Task OnMessageAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var message = ctx.Activity.Text?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        await ctx.SendActivityAsync(Activity.CreateTypingActivity(), ct);

        try
        {
            var response = await _orchestrator.HandleAsync(message, ct);
            await ctx.SendActivityAsync(FormatResponse(response), ct);

            if (response.DraftedSection is not null)
                await ctx.SendActivityAsync(
                    "📄 Full volume with formatted narratives: `POST /api/pastperformance/volume`", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PP] Pipeline error: {M}", message);
            await ctx.SendActivityAsync(
                "⚠️ An error occurred. Ensure all data sources are indexed and try again.", ct);
        }
    }

    private async Task OnHelpAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            """
            ## 🏛️ Past Performance Agent — Help

            I search across **all** configured data sources regardless of type.

            ### Commands
            | Command | Description |
            |---|---|
            | `/help` | This message |
            | `/intents` | Supported query types |
            | `/sources` | Which data sources are indexed |
            | `/status` | Service health |

            ### Filtering by source
            Mention the source type in your question:
            - "From **Deltek**, find active DoD projects over $5M"
            - "Search our **SQL database** for NAICS 541512 contracts"
            - "Find **SharePoint** PPQs for Army work"

            ### Tips
            - The agent automatically routes to the right extraction strategy per source type.
            - Structured sources (SQL, Deltek, Excel) return authoritative field data.
            - Document sources (SharePoint) return rich narrative with CPARS detail.
            """, ct);
    }

    private async Task OnIntentsAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            """
            ## 🎯 Supported Query Intents

            | Intent | Example |
            |---|---|
            | **Find Similar Contracts** | "Cloud infrastructure contracts like this JWCC task order" |
            | **Generate Volume** | "Draft a past performance volume for W912DQ-24-R-0041" |
            | **Find References** | "Who is our CO for our CMS work?" |
            | **Summarise Portfolio** | "Executive summary of our DHS past performance" |
            | **Identify Gaps** | "Do we have NAICS 541512 work in the last 5 years?" |
            | **Extract CPARS** | "CPARS ratings for all federal health IT contracts" |
            | **Find Key Personnel** | "Who has led DevSecOps programmes for DoD?" |
            | **General** | Any freeform question about past performance |

            **Source-specific:**
            - "From Deltek, show active projects with DoD" → searches Deltek only
            - "From SQL database, find contracts over $10M" → searches SQL sources only
            """, ct);
    }

    private async Task OnSourcesAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            "📊 Use `GET /api/pastperformance/sources` to see all indexed data sources with connector types.", ct);
    }

    private async Task OnStatusAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            "📊 **Status:** Past Performance Agent operational.\n" +
            "Use `/sources` to see data sources · `/help` for guidance.", ct);
    }

    private static string FormatResponse(PastPerformanceResponse response)
    {
        var sb = new StringBuilder();
        sb.AppendLine(response.Answer);

        // Always show which sources contributed
        if (response.DataSourcesSearched.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*Searched {response.DataSourcesSearched.Count} source(s): " +
                          $"{string.Join(", ", response.DataSourcesSearched)}*");
        }

        // Contract cards for find/general intents
        if (response.RelevantContracts.Count > 0
            && response.Query.Intent is not QueryIntent.GenerateVolumeSection
                                      and not QueryIntent.SummarisePortfolio
                                      and not QueryIntent.FindReferences
                                      and not QueryIntent.ExtractCPARSRatings
                                      and not QueryIntent.FindKeyPersonnel)
        {
            sb.AppendLine().AppendLine("---").AppendLine("**Relevant Contracts**");
            foreach (var c in response.RelevantContracts.Take(5))
            {
                var value  = (c.FinalObligatedValue ?? c.ContractValue) is decimal v ? $"${v:N0}" : "N/A";
                var period = c.IsOngoing ? $"{c.StartDate} – Ongoing" : $"{c.StartDate} – {c.EndDate}";
                var cpars  = string.IsNullOrEmpty(c.CPARSRatingOverall) ? "" : $" | CPARS: **{c.CPARSRatingOverall}**";
                var source = $"`{c.DataSourceName}` ({c.ConnectorType})";

                sb.AppendLine($"- **{c.ContractNumber}** — {c.AgencyAcronym ?? c.AgencyName} | {c.Title}");
                sb.AppendLine($"  {value} | {period}{cpars}");
                sb.AppendLine($"  📂 {source}" +
                              (string.IsNullOrEmpty(c.SourceDocumentUrl) ? "" : $" | [{c.SourceFileName}]({c.SourceDocumentUrl})"));
            }
        }

        if (response.Warnings.Count > 0)
        {
            sb.AppendLine().AppendLine("---").AppendLine("⚠️ **Attention Required**");
            foreach (var w in response.Warnings) sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }
}
