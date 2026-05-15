using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Services;
using System.Text;

namespace SharePointRag.PastPerformance.Workflow;

/// <summary>
/// AgentWorkflow-based Past Performance Agent.
///
/// Runs alongside the existing PastPerformanceAgent (AgentApplication) as an
/// independent, parallel implementation. Both use the same underlying services
/// (IPastPerformanceOrchestrator, IContractExtractor, IRelevanceScorer, etc.)
/// but this workflow adds:
///
///   ┌─ Multi-turn state ────────────────────────────────────────────────────────┐
///   │  PastPerformanceWorkflowState is persisted across every turn in the same │
///   │  conversation via the AgentWorkflow state store. A user can say:         │
///   │    Turn 1: "Find DoD IT modernisation contracts"                          │
///   │    Turn 2: "Now draft the volume"           ← reuses Turn 1 contracts    │
///   │    Turn 3: "Focus on the Army ones"         ← refines without new RAG    │
///   │    Turn 4: "Make the first narrative shorter"← revises existing draft    │
///   └──────────────────────────────────────────────────────────────────────────┘
///
///   ┌─ Routing logic ───────────────────────────────────────────────────────────┐
///   │  Each turn the workflow decides whether to:                               │
///   │    a) Run a new full pipeline (new RAG + extract + score + respond)       │
///   │    b) Refine on cached contracts (re-score/filter, skip RAG)             │
///   │    c) Draft or revise the current volume draft                            │
///   │    d) Answer a meta question about the session (history, sources, status) │
///   └──────────────────────────────────────────────────────────────────────────┘
///
/// Endpoint: POST /api/pastperformance/workflow/messages
/// Existing: POST /api/pastperformance/messages   ← unchanged, still works
/// </summary>
public sealed class PastPerformanceWorkflow : AgentApplication
{
    private readonly IPastPerformanceOrchestrator _orchestrator;
    private readonly IQueryParser                 _queryParser;
    private readonly IContractExtractor           _extractor;
    private readonly IRelevanceScorer             _scorer;
    private readonly IProposalDrafter             _drafter;
    private readonly ILogger<PastPerformanceWorkflow> _logger;

    // State property key — the Agents SDK stores state under this name
    private const string StateKey = "ppWorkflowState";

    public PastPerformanceWorkflow(
        AgentApplicationOptions           options,
        IPastPerformanceOrchestrator      orchestrator,
        IQueryParser                      queryParser,
        IContractExtractor                extractor,
        IRelevanceScorer                  scorer,
        IProposalDrafter                  drafter,
        ILogger<PastPerformanceWorkflow>  logger)
        : base(options)
    {
        _orchestrator = orchestrator;
        _queryParser  = queryParser;
        _extractor    = extractor;
        _scorer       = scorer;
        _drafter      = drafter;
        _logger       = logger;

        // Register activity handlers
        OnActivity(ActivityTypes.ConversationUpdate, OnConversationStartAsync);
        OnActivity(ActivityTypes.Message,            OnMessageAsync);

        // Slash commands — handled before OnMessageAsync
        OnMessage("/help",    OnHelpAsync);
        OnMessage("/history", OnHistoryAsync);
        OnMessage("/sources", OnSourcesAsync);
        OnMessage("/draft",   OnShowDraftAsync);
        OnMessage("/clear",   OnClearAsync);
        OnMessage("/status",  OnStatusAsync);
    }

    // ── Conversation start ────────────────────────────────────────────────────

    private async Task OnConversationStartAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        if (ctx.Activity.MembersAdded?.Any(m => m.Id != ctx.Activity.Recipient.Id) != true)
            return;

        var wfState = GetOrCreate(state);

        await ctx.SendActivityAsync(
            $"""
            🏛️ **Past Performance Workflow Agent**

            I maintain full conversation context across your session — no need to repeat yourself.

            **What I remember:**
            • Contracts found in previous turns
            • The current draft volume (if you've requested one)
            • Your filters and preferences

            **Try a multi-turn session:**
            > "Find DoD cloud infrastructure contracts over $10M"
            > *(next turn)* "Now draft the volume for solicitation HHS-24-001"
            > *(next turn)* "Focus on the Army contracts only"
            > *(next turn)* "Make the first narrative shorter"

            **Commands:** `/help` · `/history` · `/sources` · `/draft` · `/clear`
            Session started: {wfState.StartedAt:HH:mm UTC}
            """, ct);
    }

    // ── Main message handler ──────────────────────────────────────────────────

    private async Task OnMessageAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var message = ctx.Activity.Text?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        await ctx.SendActivityAsync(Activity.CreateTypingActivity(), ct);

        var wfState = GetOrCreate(state);
        wfState.AddTurn("user", message);

        try
        {
            // Decide which execution path to take based on message content
            // and existing session state
            var path = DetermineExecutionPath(message, wfState);

            _logger.LogInformation(
                "[Workflow] Turn {N}, path: {P}, message: {M}",
                wfState.TurnCount, path, message);

            string reply = path switch
            {
                WorkflowPath.Refine   => await HandleRefineAsync(message, wfState, ct),
                WorkflowPath.Redraft  => await HandleRedraftAsync(message, wfState, ct),
                WorkflowPath.NewQuery => await HandleNewQueryAsync(message, wfState, ct),
                _                     => await HandleNewQueryAsync(message, wfState, ct)
            };

            wfState.AddTurn("assistant", reply);
            await ctx.SendActivityAsync(reply, ct);

            // Offer shortcuts based on what was just done
            var hint = BuildContextualHint(path, wfState);
            if (!string.IsNullOrEmpty(hint))
                await ctx.SendActivityAsync(hint, ct);

            // Persist state — AgentWorkflow saves state automatically after the turn
            SaveState(state, wfState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] Turn failed: {M}", message);
            await ctx.SendActivityAsync(
                "⚠️ An error occurred. Your session context is preserved — try again or `/clear` to reset.",
                ct);
        }
    }

    // ── Execution paths ───────────────────────────────────────────────────────

    /// <summary>
    /// Full pipeline: parse → RAG → extract → score → respond.
    /// Used on the first turn or when the query is semantically new.
    /// </summary>
    private async Task<string> HandleNewQueryAsync(
        string message, PastPerformanceWorkflowState wfState, CancellationToken ct)
    {
        // Build conversation-aware query by prepending recent history
        var contextualMessage = BuildContextualMessage(message, wfState);

        // Run the full orchestrator pipeline (reuses all existing services)
        var response = await _orchestrator.HandleAsync(contextualMessage, ct);

        // Update session state with results
        wfState.LastContracts          = response.RelevantContracts.ToList();
        wfState.LastDataSourcesSearched = response.DataSourcesSearched.ToList();
        wfState.LastSemanticQuery      = response.Query.SemanticQuery;
        wfState.LastIntent             = response.Query.Intent.ToString();

        if (response.DraftedSection is not null)
        {
            wfState.CurrentDraft = response.DraftedSection;
            wfState.CurrentSolicitationContext =
                message.Contains("solicitation", StringComparison.OrdinalIgnoreCase)
                ? message : wfState.CurrentSolicitationContext;
        }

        return FormatResponse(response, wfState);
    }

    /// <summary>
    /// Refine path: re-scores/filters cached contracts without a new RAG search.
    /// Used when the user narrows, sorts, or filters the previous result set.
    /// Examples: "show only the FFP ones", "sort by CPARS", "Army contracts only".
    /// </summary>
    private async Task<string> HandleRefineAsync(
        string message, PastPerformanceWorkflowState wfState, CancellationToken ct)
    {
        if (wfState.LastContracts.Count == 0)
            return await HandleNewQueryAsync(message, wfState, ct);

        // Parse the refinement instruction to extract new filters
        var refinedQuery = await _queryParser.ParseAsync(
            $"Refine previous results: {message}\nContext: {wfState.LastSemanticQuery}", ct);

        // Apply new filters to the already-retrieved contracts
        var refined = _scorer.ScoreAndRank(
            new List<ContractRecord>(wfState.LastContracts), refinedQuery);

        wfState.LastContracts = refined;
        wfState.LastIntent    = refinedQuery.Intent.ToString();

        var sb = new StringBuilder();
        sb.AppendLine($"*Refined from {wfState.LastContracts.Count} cached contracts — no new search needed.*");
        sb.AppendLine();

        if (refined.Count == 0)
        {
            sb.AppendLine("No contracts matched your refinement criteria. Try broadening the filter or `/clear` to start fresh.");
        }
        else
        {
            sb.AppendLine($"**{refined.Count} contract(s) after refinement:**");
            foreach (var c in refined.Take(5))
            {
                var value  = (c.FinalObligatedValue ?? c.ContractValue) is decimal v ? $"${v:N0}" : "N/A";
                var period = c.IsOngoing ? $"{c.StartDate} – Ongoing" : $"{c.StartDate} – {c.EndDate}";
                var cpars  = string.IsNullOrEmpty(c.CPARSRatingOverall) ? "" : $" | CPARS: **{c.CPARSRatingOverall}**";

                sb.AppendLine($"- **{c.ContractNumber}** — {c.AgencyAcronym ?? c.AgencyName} | {c.Title}");
                sb.AppendLine($"  {value} | {period}{cpars} | `{c.DataSourceName}`");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Redraft path: revises the existing CurrentDraft based on user instruction.
    /// Used when the user wants to modify the draft without starting over.
    /// Examples: "make it shorter", "add more detail on CPARS", "focus on schedule".
    /// </summary>
    private async Task<string> HandleRedraftAsync(
        string message, PastPerformanceWorkflowState wfState, CancellationToken ct)
    {
        if (wfState.CurrentDraft is null || wfState.LastContracts.Count == 0)
            return await HandleNewQueryAsync(message, wfState, ct);

        // Build a revision instruction combining the original solicitation context
        // with the user's feedback
        var revisionContext = string.IsNullOrEmpty(wfState.CurrentSolicitationContext)
            ? message
            : $"{wfState.CurrentSolicitationContext}. Revision instruction: {message}";

        // Re-draft using the same cached contracts — no RAG, no extraction
        var newDraft = await _drafter.DraftVolumeAsync(
            wfState.LastContracts.Take(5).ToList(),
            revisionContext, ct);

        wfState.CurrentDraft = newDraft;

        var sb = new StringBuilder();
        sb.AppendLine("📋 **Revised Draft**");
        sb.AppendLine();
        sb.AppendLine($"**Executive Summary:** {newDraft.ExecutiveSummary}");
        sb.AppendLine();
        sb.AppendLine($"Revised **{newDraft.Narratives.Count}** narrative(s) based on your instruction.");
        sb.AppendLine("Use `/draft` to see the full draft or `POST /api/pastperformance/volume` to download.");

        if (newDraft.FlaggedGaps.Count > 0)
        {
            sb.AppendLine().AppendLine("⚠️ **Gaps remaining:**");
            foreach (var g in newDraft.FlaggedGaps) sb.AppendLine($"- {g}");
        }

        return sb.ToString();
    }

    // ── Path detection ────────────────────────────────────────────────────────

    private WorkflowPath DetermineExecutionPath(
        string message, PastPerformanceWorkflowState wfState)
    {
        var lower = message.ToLowerInvariant();

        // Revision intent — modifying an existing draft
        if (wfState.CurrentDraft is not null)
        {
            var revisionKeywords = new[]
            {
                "make it", "shorter", "longer", "revise", "update the draft",
                "change the", "rewrite", "edit", "modify", "improve", "fix"
            };
            if (revisionKeywords.Any(kw => lower.Contains(kw)))
                return WorkflowPath.Redraft;
        }

        // Refinement intent — narrowing/filtering/sorting cached results
        if (wfState.LastContracts.Count > 0)
        {
            var refineKeywords = new[]
            {
                "only", "filter", "sort", "narrow", "focus on", "just the",
                "show me only", "exclude", "limit to", "top ", "highest", "lowest",
                "army", "navy", "air force", "dod", "hhs", "dhs", "gsa", "doj"
            };
            bool hasRefineIntent = refineKeywords.Any(kw => lower.Contains(kw));
            bool isNewSearch     = wfState.NeedsNewSearch(message);

            if (hasRefineIntent && !isNewSearch)
                return WorkflowPath.Refine;
        }

        return WorkflowPath.NewQuery;
    }

    // ── Slash command handlers ────────────────────────────────────────────────

    private async Task OnHelpAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            """
            ## 🏛️ Past Performance Workflow — Help

            **Multi-turn aware:** I remember your contracts, filters, and draft across turns.

            ### Commands
            | Command | Description |
            |---|---|
            | `/help` | This message |
            | `/history` | Show conversation history summary |
            | `/sources` | Which data sources are indexed |
            | `/draft` | Show the current draft volume |
            | `/clear` | Reset conversation state (start fresh) |
            | `/status` | Session metadata |

            ### Multi-turn patterns
            - **Search then draft:** "Find DoD IT contracts" → "Draft the volume"
            - **Search then refine:** "Find contracts over $5M" → "Show only the FFP ones"
            - **Draft then revise:** "Draft the volume" → "Make the first narrative shorter"
            - **Follow-up questions:** "What CPARS rating did the first one get?"

            ### vs the standard agent
            The standard bot (`/api/pastperformance/messages`) is stateless.
            This workflow (`/api/pastperformance/workflow/messages`) keeps context.
            Same data sources, same extraction logic — just smarter multi-turn handling.
            """, ct);
    }

    private async Task OnHistoryAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var wfState = GetOrCreate(state);

        if (wfState.History.Count == 0)
        {
            await ctx.SendActivityAsync("No conversation history yet.", ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"## Conversation History ({wfState.TurnCount} turns)");
        sb.AppendLine();

        foreach (var turn in wfState.History.TakeLast(10))
        {
            var role    = turn.Role == "user" ? "**You**" : "**Agent**";
            var preview = turn.Content.Length > 120
                ? turn.Content[..120] + "…"
                : turn.Content;
            sb.AppendLine($"{role} _{turn.Timestamp:HH:mm}_: {preview}");
            sb.AppendLine();
        }

        if (wfState.LastContracts.Count > 0)
            sb.AppendLine($"*{wfState.LastContracts.Count} contract(s) currently cached from last search.*");

        if (wfState.CurrentDraft is not null)
            sb.AppendLine($"*Draft in progress: {wfState.CurrentDraft.Narratives.Count} narrative(s).*");

        await ctx.SendActivityAsync(sb.ToString(), ct);
    }

    private async Task OnSourcesAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var wfState = GetOrCreate(state);

        var sb = new StringBuilder();
        sb.AppendLine("## Data sources");

        if (wfState.LastDataSourcesSearched.Count > 0)
        {
            sb.AppendLine($"*Searched in last turn:* {string.Join(", ", wfState.LastDataSourcesSearched)}");
            sb.AppendLine();
        }

        sb.AppendLine("Full list: `GET /api/pastperformance/sources`");
        await ctx.SendActivityAsync(sb.ToString(), ct);
    }

    private async Task OnShowDraftAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var wfState = GetOrCreate(state);

        if (wfState.CurrentDraft is null)
        {
            await ctx.SendActivityAsync(
                "No draft yet. Ask me to draft a volume first — e.g. \"Draft the past performance volume for solicitation HHS-24-001\".",
                ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Current Draft");
        sb.AppendLine();
        sb.AppendLine($"**Executive Summary:** {wfState.CurrentDraft.ExecutiveSummary}");
        sb.AppendLine($"**Narratives:** {wfState.CurrentDraft.Narratives.Count}");
        sb.AppendLine();

        foreach (var (n, i) in wfState.CurrentDraft.Narratives.Select((n, i) => (n, i + 1)))
        {
            sb.AppendLine($"### Contract {i}: {n.Contract.ContractNumber}");
            sb.AppendLine($"*{n.Contract.AgencyName} | {n.Contract.Title}*");
            sb.AppendLine();

            // Show first 400 chars of each narrative to keep the message manageable
            var preview = n.NarrativeText.Length > 400
                ? n.NarrativeText[..400] + "\n*[truncated — download full draft via API]*"
                : n.NarrativeText;
            sb.AppendLine(preview);
            sb.AppendLine();
        }

        if (wfState.CurrentDraft.FlaggedGaps.Count > 0)
        {
            sb.AppendLine("⚠️ **Gaps flagged:**");
            foreach (var g in wfState.CurrentDraft.FlaggedGaps)
                sb.AppendLine($"- {g}");
        }

        await ctx.SendActivityAsync(sb.ToString(), ct);
    }

    private async Task OnClearAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        // Reset to a clean state — preserves nothing from the previous session
        var fresh = new PastPerformanceWorkflowState();
        SaveState(state, fresh);

        await ctx.SendActivityAsync(
            "🔄 Session cleared. History, cached contracts, and draft have been reset. Start a new search anytime.",
            ct);
    }

    private async Task OnStatusAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var wfState = GetOrCreate(state);

        await ctx.SendActivityAsync(
            $"""
            **Workflow session status**
            Started: {wfState.StartedAt:yyyy-MM-dd HH:mm} UTC
            Turns: {wfState.TurnCount}
            Cached contracts: {wfState.LastContracts.Count}
            Last intent: {wfState.LastIntent}
            Draft: {(wfState.CurrentDraft is null ? "none" : $"{wfState.CurrentDraft.Narratives.Count} narratives")}
            Last sources: {(wfState.LastDataSourcesSearched.Count > 0 ? string.Join(", ", wfState.LastDataSourcesSearched) : "none yet")}
            """, ct);
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    private static string FormatResponse(
        PastPerformanceResponse response,
        PastPerformanceWorkflowState wfState)
    {
        var sb = new StringBuilder();
        sb.AppendLine(response.Answer);

        if (response.DataSourcesSearched.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*Searched {response.DataSourcesSearched.Count} source(s): " +
                          $"{string.Join(", ", response.DataSourcesSearched)}*");
        }

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

                sb.AppendLine($"- **{c.ContractNumber}** — {c.AgencyAcronym ?? c.AgencyName} | {c.Title}");
                sb.AppendLine($"  {value} | {period}{cpars} | `{c.DataSourceName}`");
            }

            if (wfState.LastContracts.Count > 0)
                sb.AppendLine($"\n*{wfState.LastContracts.Count} contract(s) cached for follow-up refinement.*");
        }

        if (response.PluginResponses.Any(r => r.IsSuccess))
        {
            sb.AppendLine().AppendLine("---").AppendLine("**External AI insights**");
            foreach (var p in response.PluginResponses.Where(r => r.IsSuccess))
                sb.AppendLine($"*{p.PluginName}:* {p.Content[..Math.Min(200, p.Content.Length)]}…");
        }

        if (response.Warnings.Count > 0)
        {
            sb.AppendLine().AppendLine("---").AppendLine("⚠️ **Attention Required**");
            foreach (var w in response.Warnings) sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }

    private static string BuildContextualMessage(
        string message, PastPerformanceWorkflowState wfState)
    {
        if (wfState.History.Count <= 1)
            return message;  // first turn — no prior context to inject

        // Summarise the last 2 assistant turns to give the orchestrator context
        // without blowing up the token budget
        var recentAssistant = wfState.History
            .Where(t => t.Role == "assistant")
            .TakeLast(2)
            .Select(t => t.Content.Length > 300 ? t.Content[..300] + "…" : t.Content)
            .ToList();

        if (recentAssistant.Count == 0) return message;

        return $"{message}\n\n[Prior context: {string.Join(" | ", recentAssistant)}]";
    }

    private static string BuildContextualHint(
        WorkflowPath path, PastPerformanceWorkflowState wfState)
    {
        return path switch
        {
            WorkflowPath.NewQuery when wfState.LastContracts.Count > 0
                && wfState.CurrentDraft is null
                => $"💡 *{wfState.LastContracts.Count} contracts cached. Ask me to draft the volume, refine the results, or filter by agency/type.*",

            WorkflowPath.NewQuery when wfState.CurrentDraft is not null
                => "💡 *Draft updated. Say \"make it shorter\", \"add more CPARS detail\", or `/draft` to review.*",

            WorkflowPath.Refine
                => $"💡 *{wfState.LastContracts.Count} contracts after refinement. Ask me to draft a volume or refine further.*",

            WorkflowPath.Redraft
                => "💡 *Draft revised. Say `/draft` to review the full version or continue refining.*",

            _ => string.Empty
        };
    }

    // ── State accessors ───────────────────────────────────────────────────────

    private static PastPerformanceWorkflowState GetOrCreate(ITurnState state)
    {
        if (state.Conversation.TryGetValue(StateKey, out var obj)
            && obj is PastPerformanceWorkflowState existing)
            return existing;

        var fresh = new PastPerformanceWorkflowState();
        state.Conversation[StateKey] = fresh;
        return fresh;
    }

    private static void SaveState(ITurnState state, PastPerformanceWorkflowState wfState)
    {
        state.Conversation[StateKey] = wfState;
    }
}

/// <summary>Which execution path the workflow chose for this turn.</summary>
internal enum WorkflowPath
{
    NewQuery,   // full RAG + extract + score + respond
    Refine,     // re-score/filter cached contracts, skip RAG
    Redraft     // revise existing draft, skip RAG and extraction
}
