using System.ComponentModel.DataAnnotations;

namespace SF1449ContractManager.Core.Models;

/// <summary>
/// One entry per header field the AI extraction agent populated. This is what powers
/// the "here's what I found and where" review screen: each field gets a confidence
/// score, the source page it was read from, and a flag for whether a human has
/// confirmed it. Line items and clauses carry their own confidence implicitly via
/// their own tables, but header fields are numerous/scalar so we track them here
/// instead of adding 60 nullable "XConfidence" columns to Sf1449Contract.
/// </summary>
public class FieldExtraction
{
    [Key]
    public int Id { get; set; }

    public int Sf1449ContractId { get; set; }
    public Sf1449Contract? Sf1449Contract { get; set; }

    /// <summary>Matches the CLR property name on <see cref="Sf1449Contract"/>, e.g. "ContractNumber".</summary>
    [Required]
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Human-readable label for the UI, e.g. "Block 2 - Contract Number".</summary>
    public string? DisplayLabel { get; set; }

    public string? ExtractedValueRaw { get; set; }
    public double Confidence { get; set; } // 0.0 - 1.0
    public int? SourcePageNumber { get; set; }
    public bool ReviewedByUser { get; set; }
    public string? ReviewerNote { get; set; }

    // --- On-page location, used to draw the highlight box directly on the PDF ------------
    // Expressed as percentages (0.0-1.0) of the source page's width/height, top-left origin,
    // so the overlay scales with the rendered canvas regardless of zoom level. Populated by
    // FieldLocator after extraction; null if the value's text couldn't be confidently located
    // on the page (the field still shows in the side list either way, it just won't draw a box).
    public double? BoxLeftPct { get; set; }
    public double? BoxTopPct { get; set; }
    public double? BoxWidthPct { get; set; }
    public double? BoxHeightPct { get; set; }

    public bool HasBoundingBox => BoxLeftPct is not null && BoxTopPct is not null
        && BoxWidthPct is not null && BoxHeightPct is not null;

    /// <summary>Confidence bucket used purely for UI color-coding (green/yellow/red highlight).</summary>
    public FieldConfidenceLevel Level =>
        Confidence >= 0.85 ? FieldConfidenceLevel.High :
        Confidence >= 0.55 ? FieldConfidenceLevel.Medium :
        FieldConfidenceLevel.Low;
}

public enum FieldConfidenceLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}
