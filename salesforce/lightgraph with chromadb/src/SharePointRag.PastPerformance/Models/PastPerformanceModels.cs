using System.Text.Json.Serialization;

namespace SharePointRag.PastPerformance.Models;

// ── Contract / Award ──────────────────────────────────────────────────────────

/// <summary>
/// A single past-performance contract record, regardless of where it came from.
/// Can be extracted from a SharePoint document (via LLM), a SQL row (direct mapping),
/// an Excel spreadsheet (column mapping), a Deltek API response, or any custom source.
/// </summary>
public record ContractRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    // ── Source provenance ─────────────────────────────────────────────────────
    /// <summary>Which named data source this record came from (e.g. "DeltekVantagepoint").</summary>
    public string DataSourceName { get; init; } = string.Empty;

    /// <summary>The connector type that produced this record.</summary>
    public string ConnectorType { get; init; } = string.Empty;

    // ── Identification ────────────────────────────────────────────────────────
    /// <summary>Contract / Task Order number (e.g. W912DQ-21-C-0042)</summary>
    public string ContractNumber { get; init; } = string.Empty;

    /// <summary>Parent IDIQ/GWAC if this is a task order</summary>
    public string? ParentContractNumber { get; init; }

    /// <summary>Contract type: FFP, CPFF, T&M, IDIQ, BPA, etc.</summary>
    public string ContractType { get; init; } = string.Empty;

    // ── Customer ──────────────────────────────────────────────────────────────
    public string AgencyName { get; init; } = string.Empty;
    public string? AgencyAcronym { get; init; }
    public string? ContractingOfficer { get; init; }
    public string? ContractingOfficerPhone { get; init; }
    public string? ContractingOfficerEmail { get; init; }
    public string? COR { get; init; }
    public string? CORPhone { get; init; }
    public string? COREmail { get; init; }

    // ── Scope & Value ─────────────────────────────────────────────────────────
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> NaicsCodes { get; init; } = [];
    public List<string> PscCodes { get; init; } = [];
    public decimal? ContractValue { get; init; }
    public decimal? FinalObligatedValue { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public bool IsOngoing { get; init; }

    // ── Performance ───────────────────────────────────────────────────────────
    public string? CPARSRatingOverall { get; init; }
    public string? CPARSRatingQuality { get; init; }
    public string? CPARSRatingSchedule { get; init; }
    public string? CPARSRatingCostControl { get; init; }
    public string? CPARSRatingManagement { get; init; }
    public string? CPARSRatingSmallBusiness { get; init; }
    public List<string> KeyAccomplishments { get; init; } = [];
    public List<string> ChallengesAndResolutions { get; init; } = [];

    // ── Team ──────────────────────────────────────────────────────────────────
    public string PerformingEntity { get; init; } = string.Empty;
    public List<string> Subcontractors { get; init; } = [];
    public List<string> TeammateRoles { get; init; } = [];
    public List<KeyPersonnel> KeyPersonnel { get; init; } = [];

    // ── Relevance scoring (populated at query time, not persisted) ────────────
    [JsonIgnore] public double RelevanceScore { get; set; }
    [JsonIgnore] public string? SourceDocumentUrl { get; set; }
    [JsonIgnore] public string? SourceFileName { get; set; }

    /// <summary>
    /// Raw metadata from the source connector (SQL columns, Deltek fields, Excel cells).
    /// Preserved so the LLM and UI can surface extra context beyond the mapped fields.
    /// </summary>
    public Dictionary<string, string> SourceMetadata { get; init; } = [];
}

public record KeyPersonnel(
    string Name,
    string Title,
    string? Clearance = null,
    string? Role = null
);

// ── Query intent ──────────────────────────────────────────────────────────────

public record PastPerformanceQuery
{
    public string RawQuestion { get; init; } = string.Empty;
    public string SemanticQuery { get; init; } = string.Empty;
    public QueryIntent Intent { get; init; }
    public string? AgencyFilter { get; init; }
    public string? NaicsFilter { get; init; }
    public string? ContractTypeFilter { get; init; }
    public decimal? MinValueFilter { get; init; }
    public int? RecencyYearsFilter { get; init; }
    public string? KeywordFilter { get; init; }
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Optional filter to restrict results to specific connector types.
    /// e.g. ["Deltek","SqlDatabase"] to only show structured source records.
    /// Empty = all sources.
    /// </summary>
    public List<string> ConnectorTypeFilter { get; init; } = [];

    /// <summary>
    /// Optional filter to restrict results to specific named data sources.
    /// e.g. ["DeltekVantagepoint","DeltekCostpointContracts"]
    /// Empty = all sources.
    /// </summary>
    public List<string> DataSourceFilter { get; init; } = [];
}

public enum QueryIntent
{
    FindSimilarContracts,
    GenerateVolumeSection,
    FindReferences,
    SummarisePortfolio,
    IdentifyGaps,
    ExtractCPARSRatings,
    FindKeyPersonnel,
    General
}

// ── Proposal output ───────────────────────────────────────────────────────────

public record PastPerformanceVolumeSection
{
    public string SolicitationReference { get; init; } = string.Empty;
    public List<ContractNarrative> Narratives { get; init; } = [];
    public string ExecutiveSummary { get; init; } = string.Empty;
    public List<string> FlaggedGaps { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record ContractNarrative
{
    public ContractRecord Contract { get; init; } = new();
    public string RelevanceRationale { get; init; } = string.Empty;
    public string NarrativeText { get; init; } = string.Empty;
    public string ReferenceBlock { get; init; } = string.Empty;
}

public record PastPerformanceResponse
{
    public PastPerformanceQuery Query { get; init; } = new();
    public string Answer { get; init; } = string.Empty;
    public List<ContractRecord> RelevantContracts { get; init; } = [];
    public PastPerformanceVolumeSection? DraftedSection { get; init; }
    public List<string> Citations { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    /// <summary>Which data source names were searched for this response.</summary>
    public List<string> DataSourcesSearched { get; init; } = [];
}
