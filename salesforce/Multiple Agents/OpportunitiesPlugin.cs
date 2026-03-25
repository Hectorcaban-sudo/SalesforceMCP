using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ChatApp.Plugins;

/// <summary>
/// Simulated Salesforce Opportunities plugin.
/// In production, replace method bodies with real Salesforce API / SOQL calls.
/// </summary>
public sealed class OpportunitiesPlugin
{
    [KernelFunction, Description("Get a list of Salesforce opportunities, optionally filtered by stage or account.")]
    public string GetOpportunities(
        [Description("Optional stage filter e.g. Prospecting, Qualification, Proposal, Closed Won")] string? stage = null,
        [Description("Optional account name to filter by")] string? accountName = null)
    {
        // TODO: SELECT Id, Name, StageName, Amount, CloseDate, AccountId FROM Opportunity WHERE ...
        return $"""
            [Opportunities]
            - OPP-001 | Acme Corp Renewal     | Proposal/Price Quote | $120,000 | Close: 2025-06-30
            - OPP-002 | Globex New License     | Qualification        | $45,000  | Close: 2025-07-15
            - OPP-003 | Initech Expansion      | Closed Won           | $200,000 | Close: 2025-04-01
            - OPP-004 | Acme Add-on Services   | Prospecting          | $30,000  | Close: 2025-08-01
            Filter applied → stage: '{stage ?? "any"}', account: '{accountName ?? "any"}'
            """;
    }

    [KernelFunction, Description("Get details of a specific Salesforce opportunity.")]
    public string GetOpportunityDetail(
        [Description("Opportunity name or Salesforce Opportunity ID")] string opportunityId)
    {
        // TODO: SELECT * FROM Opportunity WHERE Id = :opportunityId
        return $"""
            [Opportunity Detail: {opportunityId}]
            Name:        Acme Corp Renewal
            Account:     Acme Corp
            Stage:       Proposal/Price Quote
            Amount:      $120,000
            Close Date:  2025-06-30
            Probability: 60%
            Owner:       Bob Jones
            Next Step:   Send updated proposal
            """;
    }

    [KernelFunction, Description("Update the stage or amount of a Salesforce opportunity.")]
    public string UpdateOpportunity(
        [Description("Opportunity name or ID")] string opportunityId,
        [Description("New stage name")] string? stage = null,
        [Description("New amount in dollars")] decimal? amount = null)
    {
        // TODO: UPDATE Opportunity SET StageName = :stage, Amount = :amount WHERE Id = :opportunityId
        var updates = new List<string>();
        if (stage  != null) updates.Add($"Stage → '{stage}'");
        if (amount != null) updates.Add($"Amount → ${amount:N0}");
        return $"[Opportunity Updated] {opportunityId}: {string.Join(", ", updates)}";
    }

    [KernelFunction, Description("Create a new Salesforce opportunity linked to an account.")]
    public string CreateOpportunity(
        [Description("Opportunity name")] string name,
        [Description("Account name")] string accountName,
        [Description("Stage name")] string stage,
        [Description("Expected close date (YYYY-MM-DD)")] string closeDate,
        [Description("Amount in dollars")] decimal amount)
    {
        // TODO: INSERT into Opportunity
        return $"[Opportunity Created] '{name}' for {accountName} | {stage} | ${amount:N0} | Close: {closeDate} — ID: OPP-{Guid.NewGuid():N[..8]}";
    }
}
