using System.ComponentModel.DataAnnotations;

namespace SF1449ContractManager.Core.Models;

/// <summary>
/// A FAR/DFARS/agency clause or provision referenced anywhere in the package:
/// "Contract Clauses", "Addendum to Contract Clauses", "Solicitation Provisions",
/// "Addendum to Solicitation Provisions", or full-text clauses like 52.212-5 with
/// checkbox sub-paragraphs. One row per clause number per section, so the same
/// clause number can legitimately appear more than once if it shows up in multiple
/// sections of the document.
/// </summary>
public class ContractClause
{
    [Key]
    public int Id { get; set; }

    public int Sf1449ContractId { get; set; }
    public Sf1449Contract? Sf1449Contract { get; set; }

    [Required]
    public string ClauseNumber { get; set; } = string.Empty; // e.g. "52.212-4", "252.204-7012"
    public string? Title { get; set; }
    public string? EffectiveDate { get; set; }   // kept as printed text, e.g. "Nov 2023" (not always a full date)
    public string? AlternateOrDeviation { get; set; }
    public string? DeviationEffectiveDate { get; set; }

    public ClauseCategory Category { get; set; } = ClauseCategory.FAR;
    public ClauseIncorporationType IncorporationType { get; set; } = ClauseIncorporationType.ByReference;
    public ClauseSection Section { get; set; } = ClauseSection.ContractClauses;

    /// <summary>True when the clause's checkbox on the 52.212-5 addendum was marked (applicable).</summary>
    public bool IsChecked { get; set; }

    /// <summary>Populated only for clauses "Incorporated by Full Text" (e.g. 52.219-28, 52.212-3, 52.252-2).</summary>
    public string? FullText { get; set; }
}
