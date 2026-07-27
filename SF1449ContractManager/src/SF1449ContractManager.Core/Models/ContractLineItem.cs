using System.ComponentModel.DataAnnotations;

namespace SF1449ContractManager.Core.Models;

/// <summary>
/// One row of Block 19-24, "Schedule of Supplies/Services" (CLIN). Also used for
/// rows pulled from an attached Excel Bid Schedule when one is referenced/attached.
/// </summary>
public class ContractLineItem
{
    [Key]
    public int Id { get; set; }

    public int Sf1449ContractId { get; set; }
    public Sf1449Contract? Sf1449Contract { get; set; }

    public int SortOrder { get; set; }

    public string? ItemNumber { get; set; }       // Block 19
    public string? Description { get; set; }       // Block 20
    public decimal? Quantity { get; set; }          // Block 21
    public string? Unit { get; set; }                // Block 22
    public decimal? UnitPrice { get; set; }           // Block 23
    public decimal? Amount { get; set; }               // Block 24

    public string? FrequencyOfService { get; set; }     // e.g. "2x weekly", "Monthly", "Annual"
    public string? PerformanceLocation { get; set; }     // e.g. "940 Boston Providence Hwy, Norwood MA"
}
