using SharePointRag.PastPerformance.Models;
using System.Text.Json.Serialization;

namespace SharePointRag.PastPerformance.Workflow;

/// <summary>
/// Persistent conversation state carried across every turn of the
/// PastPerformanceWorkflow.
///
/// The Agents SDK serialises this to its IStorage backend (in-memory by default,
/// replaceable with Azure Blob, CosmosDB, etc.) keyed by conversation ID.
/// Every field here survives between user messages in the same conversation.
///
/// This is the key difference from the stateless PastPerformanceAgent (AgentApplication):
/// a user can say "find DoD cloud contracts" then "now draft the volume" then
/// "focus on Army only" and the workflow remembers every prior result without
/// re-running RAG from scratch.
/// </summary>
public sealed class PastPerformanceWorkflowState
{
    // ── Conversation history ──────────────────────────────────────────────────

    /// <summary>
    /// Ordered history of (role, content) turns in this conversation.
    /// Used to build the multi-turn context window for each LLM call so the
    /// model can answer follow-up questions coherently.
    /// Capped at MaxHistoryTurns to bound memory and token cost.
    /// </summary>
    public List<ConversationTurn> History { get; set; } = [];

    /// <summary>Maximum turns kept in the rolling history window. Default: 20.</summary>
    public int MaxHistoryTurns { get; set; } = 20;

    // ── Last search results ───────────────────────────────────────────────────

    /// <summary>
    /// Contracts retrieved and ranked in the most recent search turn.
    /// Subsequent turns can refine ("show only the FFP ones"), re-rank
    /// ("sort by CPARS rating"), or draft from these without a new RAG call.
    /// </summary>
    public List<ContractRecord> LastContracts { get; set; } = [];

    /// <summary>Data sources searched in the most recent turn.</summary>
    public List<string> LastDataSourcesSearched { get; set; } = [];

    /// <summary>
    /// The semantic query used for the most recent vector search.
    /// Stored so the workflow can decide whether a follow-up question
    /// is semantically different enough to warrant a new RAG call.
    /// </summary>
    public string LastSemanticQuery { get; set; } = string.Empty;

    /// <summary>Intent detected in the most recent turn.</summary>
    public string LastIntent { get; set; } = string.Empty;

    // ── Draft state ───────────────────────────────────────────────────────────

    /// <summary>
    /// The most recently drafted Past Performance Volume section.
    /// Preserved across turns so the user can request revisions
    /// ("make it shorter", "add the COR email for the first contract")
    /// without re-drafting from scratch.
    /// </summary>
    public PastPerformanceVolumeSection? CurrentDraft { get; set; }

    /// <summary>
    /// Solicitation context used to generate CurrentDraft.
    /// Stored so re-draft calls can reuse it without asking the user again.
    /// </summary>
    public string CurrentSolicitationContext { get; set; } = string.Empty;

    // ── Session metadata ──────────────────────────────────────────────────────

    /// <summary>When this conversation was started.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Total number of turns processed in this conversation.</summary>
    public int TurnCount { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends a turn to the history, trimming the oldest entries when the
    /// window is full.
    /// </summary>
    public void AddTurn(string role, string content)
    {
        History.Add(new ConversationTurn(role, content, DateTimeOffset.UtcNow));
        while (History.Count > MaxHistoryTurns)
            History.RemoveAt(0);
        TurnCount++;
    }

    /// <summary>
    /// Returns true when the new semantic query is substantially different
    /// from the last one, indicating a new RAG search is needed.
    /// Simple heuristic: Jaccard similarity on trigrams below threshold.
    /// </summary>
    public bool NeedsNewSearch(string newQuery, double threshold = 0.6)
    {
        if (string.IsNullOrEmpty(LastSemanticQuery) || LastContracts.Count == 0)
            return true;

        var a = Trigrams(LastSemanticQuery);
        var b = Trigrams(newQuery);
        if (a.Count == 0 || b.Count == 0) return true;

        var intersection = a.Intersect(b).Count();
        var union        = a.Union(b).Count();
        double jaccard   = (double)intersection / union;

        return jaccard < threshold;
    }

    private static HashSet<string> Trigrams(string s)
    {
        s = s.ToLowerInvariant().Trim();
        var set = new HashSet<string>();
        for (int i = 0; i <= s.Length - 3; i++)
            set.Add(s.Substring(i, 3));
        return set;
    }
}

/// <summary>A single turn in the conversation history.</summary>
public sealed record ConversationTurn(
    string Role,       // "user" | "assistant"
    string Content,
    DateTimeOffset Timestamp
);
