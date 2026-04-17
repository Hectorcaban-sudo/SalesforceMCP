using System.Text.Json.Serialization;

namespace SharePointRag.PastPerformance.Models;

// ── Contract / Award ──────────────────────────────────────────────────────────

/// <summary>
/// A single past-performance contract record extracted from source documents.
/// Maps to FAR 15.305(a)(2) evaluation factors and CPARS/PPIRS data elements.
/// </summary>
public record ContractRecord
{
    // ── Identification ────────────────────────────────────────────────────────
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Contract / Task Order number (e.g. W912DQ-21-C-0042)</summary>
    public string ContractNumber { get; init; } = string.Empty;

    /// <summary>Parent IDIQ/GWAC if this is a task order</summary>
    public string? ParentContractNumber { get; init; }

    /// <summary>Contract type: FFP, CPFF, T&M, IDIQ, BPA, etc.</summary>
    public string ContractType { get; init; } = string.Empty;

    // ── Customer ──────────────────────────────────────────────────────────────
    public string AgencyName { get; init; } = string.Empty;
    public string? AgencyAcronym { get; init; }

    /// <summary>Contracting Officer name</summary>
    public string? ContractingOfficer { get; init; }

    /// <summary>Contracting Officer contact info</summary>
    public string? ContractingOfficerPhone { get; init; }
    public string? ContractingOfficerEmail { get; init; }

    /// <summary>Program / Contracting Officer's Representative</summary>
    public string? COR { get; init; }
    public string? CORPhone { get; init; }
    public string? COREmail { get; init; }

    // ── Scope & Value ─────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>NAICS code(s) for this effort</summary>
    public List<string> NaicsCodes { get; init; } = [];

    /// <summary>PSC / FSC product-service codes</summary>
    public List<string> PscCodes { get; init; } = [];

    /// <summary>Original contract value (USD)</summary>
    public decimal? ContractValue { get; init; }

    /// <summary>Final obligated value including modifications</summary>
    public decimal? FinalObligatedValue { get; init; }

    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public bool IsOngoing { get; init; }

    // ── Performance ───────────────────────────────────────────────────────────
    /// <summary>CPARS ratings: Exceptional / Very Good / Satisfactory / Marginal / Unsatisfactory</summary>
    public string? CPARSRatingOverall { get; init; }
    public string? CPARSRatingQuality { get; init; }
    public string? CPARSRatingSchedule { get; init; }
    public string? CPARSRatingCostControl { get; init; }
    public string? CPARSRatingManagement { get; init; }
    public string? CPARSRatingSmallBusiness { get; init; }

    /// <summary>Key accomplishments and measurable outcomes</summary>
    public List<string> KeyAccomplishments { get; init; } = [];

    /// <summary>Challenges encountered and how they were resolved</summary>
    public List<string> ChallengesAndResolutions { get; init; } = [];

    // ── Team ──────────────────────────────────────────────────────────────────
    public string PerformingEntity { get; init; } = string.Empty;
    public List<string> Subcontractors { get; init; } = [];
    public List<string> TeammateRoles { get; init; } = [];

    /// <summary>Key personnel who worked on this contract</summary>
    public List<KeyPersonnel> KeyPersonnel { get; init; } = [];

    // ── Relevance scoring (populated at query time) ───────────────────────────
    [JsonIgnore] public double RelevanceScore { get; set; }
    [JsonIgnore] public string? SourceDocumentUrl { get; set; }
    [JsonIgnore] public string? SourceFileName { get; set; }
}

public record KeyPersonnel(
    string Name,
    string Title,
    string? Clearance = null,
    string? Role = null
);

// ── Query Intent ──────────────────────────────────────────────────────────────

/// <summary>
/// Structured interpretation of a user's past-performance query, extracted
/// by the LLM before running vector search so filters can be applied.
/// </summary>
public record PastPerformanceQuery
{
    public string RawQuestion { get; init; } = string.Empty;

    /// <summary>Free-text semantic query sent to vector search</summary>
    public string SemanticQuery { get; init; } = string.Empty;

    /// <summary>Detected intent category</summary>
    public QueryIntent Intent { get; init; }

    // Optional filters
    public string? AgencyFilter { get; init; }
    public string? NaicsFilter { get; init; }
    public string? ContractTypeFilter { get; init; }
    public decimal? MinValueFilter { get; init; }
    public int? RecencyYearsFilter { get; init; }
    public string? KeywordFilter { get; init; }
    public int TopK { get; init; } = 5;
}

public enum QueryIntent
{
    FindSimilarContracts,       // "Show me IT modernisation work similar to this SOW"
    GenerateVolumeSection,      // "Draft a past performance volume for this RFP"
    FindReferences,             // "Who is our CO reference for Army work?"
    SummarisePortfolio,         // "What's our DoD past performance summary?"
    IdentifyGaps,               // "Do we have relevant NAICS 541512 work?"
    ExtractCPARSRatings,        // "What are our CPARS ratings for HHS work?"
    FindKeyPersonnel,           // "Who has led similar cloud migration efforts?"
    General                     // Catch-all
}

// ── Proposal Output ───────────────────────────────────────────────────────────

/// <summary>A fully drafted past-performance volume section ready for RFP submission.</summary>
public record PastPerformanceVolumeSection
{
    /// <summary>Which RFP / solicitation this was drafted for</summary>
    public string SolicitationReference { get; init; } = string.Empty;

    /// <summary>Ordered list of contract narratives with relevance rationale</summary>
    public List<ContractNarrative> Narratives { get; init; } = [];

    /// <summary>Executive relevance summary paragraph (for Volume cover)</summary>
    public string ExecutiveSummary { get; init; } = string.Empty;

    /// <summary>Any gaps the agent flagged (contracts referenced but not in library)</summary>
    public List<string> FlaggedGaps { get; init; } = [];

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record ContractNarrative
{
    public ContractRecord Contract { get; init; } = new();

    /// <summary>One-paragraph relevance rationale explaining how this contract maps to the SOW</summary>
    public string RelevanceRationale { get; init; } = string.Empty;

    /// <summary>Formatted narrative text (ready to paste into proposal)</summary>
    public string NarrativeText { get; init; } = string.Empty;

    /// <summary>Suggested CPARS reference contact block</summary>
    public string ReferenceBlock { get; init; } = string.Empty;
}

// ── Agent Response ────────────────────────────────────────────────────────────

/// <summary>Unified response from the Past Performance Agent to any query.</summary>
public record PastPerformanceResponse
{
    public PastPerformanceQuery Query { get; init; } = new();
    public string Answer { get; init; } = string.Empty;
    public List<ContractRecord> RelevantContracts { get; init; } = [];
    public PastPerformanceVolumeSection? DraftedSection { get; init; }
    public List<string> Citations { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
