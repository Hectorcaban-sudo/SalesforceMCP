namespace SF1449ContractManager.Core.Extraction;

/// <summary>One header field as returned by the LLM: value + how sure it is + where it read it from.</summary>
public class ExtractedField
{
    public string? Value { get; set; }
    public double Confidence { get; set; }
    public int? SourcePage { get; set; }
}

public class ExtractedLineItem
{
    public string? ItemNumber { get; set; }
    public string? Description { get; set; }
    public string? Quantity { get; set; }
    public string? Unit { get; set; }
    public string? UnitPrice { get; set; }
    public string? Amount { get; set; }
    public string? FrequencyOfService { get; set; }
    public string? PerformanceLocation { get; set; }
    public int? SourcePage { get; set; }
}

public class ExtractedClause
{
    public string ClauseNumber { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? EffectiveDate { get; set; }
    public string? Category { get; set; }          // "FAR" | "DFARS" | "Agency" | "Other"
    public string? IncorporationType { get; set; }  // "ByReference" | "FullText"
    public string? Section { get; set; }             // matches ClauseSection enum names
    public bool IsChecked { get; set; }
    public int? SourcePage { get; set; }
}

/// <summary>
/// Root JSON shape requested from the LLM. Keys in HeaderFields must match the CLR
/// property names on Sf1449Contract - see <see cref="ExtractionPrompts.HeaderFieldNames"/>.
/// </summary>
public class Sf1449ExtractionResponse
{
    public Dictionary<string, ExtractedField> HeaderFields { get; set; } = new();
    public List<ExtractedLineItem> LineItems { get; set; } = new();
    public List<ExtractedClause> Clauses { get; set; } = new();
}
