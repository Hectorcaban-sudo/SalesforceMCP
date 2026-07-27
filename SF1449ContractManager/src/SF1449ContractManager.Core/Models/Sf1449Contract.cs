using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SF1449ContractManager.Core.Models;

/// <summary>
/// One SF-1449 "Solicitation/Contract/Order for Commercial Products and Commercial
/// Services" package. Property names/comments reference the block numbers printed on
/// the form so field-mapping is auditable against the source PDF.
/// </summary>
public class Sf1449Contract
{
    [Key]
    public int Id { get; set; }

    // --- Document identity / provenance ---------------------------------
    public string? SourcePdfFileName { get; set; }
    public string? SourcePdfStoragePath { get; set; }
    public int PageCount { get; set; }
    public ContractStatus Status { get; set; } = ContractStatus.Draft;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? ExtractedAtUtc { get; set; }
    public string? Title { get; set; } // e.g. "Janitorial Services, Recruiting Centers, Norwood and Quincy, MA"

    // --- Block 1-8 : Solicitation identity --------------------------------
    public string? RequisitionNumber { get; set; }          // Block 1
    public string? ContractNumber { get; set; }              // Block 2
    public DateTime? AwardEffectiveDate { get; set; }        // Block 3
    public string? OrderNumber { get; set; }                  // Block 4
    public string? SolicitationNumber { get; set; }           // Block 5
    public DateTime? SolicitationIssueDate { get; set; }      // Block 6
    public string? SolicitationContactName { get; set; }      // Block 7a
    public string? SolicitationContactPhone { get; set; }     // Block 7b
    public DateTime? OfferDueDateLocalTime { get; set; }      // Block 8

    // --- Block 9 : Issued By ----------------------------------------------
    public string? IssuedByCode { get; set; }
    public string? IssuedByName { get; set; }
    public string? IssuedByAddress { get; set; }
    public string? IssuedByContactEmail { get; set; }

    // --- Block 10 : Acquisition type / set-aside / NAICS -------------------
    public AcquisitionType AcquisitionType { get; set; } = AcquisitionType.Unrestricted;
    public decimal? SetAsidePercent { get; set; }
    public bool IsSmallBusiness { get; set; }
    public bool IsHubZoneSmallBusiness { get; set; }
    public bool IsServiceDisabledVeteranOwned { get; set; }
    public bool IsWomenOwnedSmallBusiness { get; set; }
    public bool IsEconomicallyDisadvantagedWomenOwned { get; set; }
    public bool Is8A { get; set; }
    public string? NaicsCode { get; set; }
    public decimal? SizeStandardUsd { get; set; }

    // --- Block 11-14 --------------------------------------------------------
    public bool FobDestinationSeeSchedule { get; set; } = true; // Block 11
    public string? DiscountTerms { get; set; }                  // Block 12
    public bool IsDpasRatedOrder { get; set; }                  // Block 13a
    public string? DpasRating { get; set; }                     // Block 13b
    public MethodOfSolicitation MethodOfSolicitation { get; set; } = MethodOfSolicitation.Unspecified; // Block 14

    // --- Block 15-16 : Deliver To / Administered By -------------------------
    public string? DeliverToCode { get; set; }
    public string? DeliverToAddress { get; set; }
    public string? AdministeredByCode { get; set; }
    public string? AdministeredByAddress { get; set; }

    // --- Block 17 : Contractor/Offeror --------------------------------------
    public string? ContractorCode { get; set; }         // CAGE code, Block 17a
    public string? ContractorFacilityCode { get; set; }
    public string? ContractorName { get; set; }
    public string? ContractorAddress { get; set; }
    public string? ContractorPhone { get; set; }
    public bool RemittanceAddressDiffers { get; set; }   // Block 17b

    // --- Block 18 : Payment ---------------------------------------------------
    public string? PaymentWillBeMadeByCode { get; set; } // Block 18a
    public string? PaymentWillBeMadeByAddress { get; set; }
    public bool SubmitInvoicesSeeAddendum { get; set; }   // Block 18b

    // --- Block 19-24 : see ContractLineItems navigation collection ----------

    // --- Block 25-26 -----------------------------------------------------------
    public string? AccountingAndAppropriationData { get; set; } // Block 25, often "SEE CONTINUATION"
    public decimal? TotalAwardAmount { get; set; }               // Block 26 (Government use only)

    // --- Block 27 : FAR incorporation flags -------------------------------------
    public bool SolicitationIncorporatesFarByReference { get; set; }  // 27a
    public bool SolicitationAddendaAttached { get; set; }
    public bool ContractIncorporatesFarByReference { get; set; }      // 27b
    public bool ContractAddendaAttached { get; set; }

    // --- Block 28-29 -------------------------------------------------------------
    public bool ContractorSignatureRequired { get; set; }  // 28
    public string? AwardReferenceOfferDate { get; set; }    // 29

    // --- Block 30 : Offeror signature ----------------------------------------------
    public string? OfferorSignerNameAndTitle { get; set; }
    public DateTime? OfferorSignedDate { get; set; }

    // --- Block 31 : Contracting officer signature -----------------------------------
    public string? ContractingOfficerName { get; set; }
    public DateTime? ContractingOfficerSignedDate { get; set; }

    // --- Free-text narrative sections pulled from the continuation pages --------------
    public string? InstructionsToVendors { get; set; }
    public string? ScopeOfWorkSummary { get; set; }
    public string? PeriodOfPerformanceSummary { get; set; }
    public string? WageDeterminationNumber { get; set; }
    public string? WageDeterminationUrl { get; set; }

    // --- Navigation ---------------------------------------------------------------------
    public List<ContractLineItem> LineItems { get; set; } = new();
    public List<ContractClause> Clauses { get; set; } = new();
    public List<FieldExtraction> FieldExtractions { get; set; } = new();

    [NotMapped]
    public string DisplayName => !string.IsNullOrWhiteSpace(ContractNumber)
        ? ContractNumber!
        : SolicitationNumber ?? $"Contract #{Id}";
}
