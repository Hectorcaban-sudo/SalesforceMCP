using System.ComponentModel;

namespace ChatApp.Plugins;

/// <summary>
/// Salesforce Opportunities tools — registered with AIFunctionFactory.Create().
/// </summary>
public static class OpportunitiesTools
{
    [Description("Get a list of Salesforce opportunities, optionally filtered by stage or account.")]
    public static string GetOpportunities(
        [Description("Stage filter e.g. Prospecting, Qualification, Proposal, Closed Won")] string? stage = null,
        [Description("Account name filter")] string? accountName = null)
    {
        // TODO: SELECT Id, Name, StageName, Amount, CloseDate FROM Opportunity WHERE ...
        return $"""
            [Opportunities]
            - OPP-001 | Acme Corp Renewal     | Proposal/Price Quote | $120,000 | Close: 2025-06-30
            - OPP-002 | Globex New License     | Qualification        | $45,000  | Close: 2025-07-15
            - OPP-003 | Initech Expansion      | Closed Won           | $200,000 | Close: 2025-04-01
            - OPP-004 | Acme Add-on Services   | Prospecting          | $30,000  | Close: 2025-08-01
            Filter → stage: '{stage ?? "any"}', account: '{accountName ?? "any"}'
            """;
    }

    [Description("Get details of a specific Salesforce opportunity by name or ID.")]
    public static string GetOpportunityDetail(
        [Description("Opportunity name or Salesforce ID")] string opportunityId)
    {
        return $"""
            [Opportunity: {opportunityId}]
            Account: Acme Corp | Stage: Proposal/Price Quote | Amount: $120,000
            Close: 2025-06-30 | Probability: 60% | Owner: Bob Jones
            Next Step: Send updated proposal
            """;
    }

    [Description("Update the stage or amount of a Salesforce opportunity.")]
    public static string UpdateOpportunity(
        [Description("Opportunity name or ID")] string opportunityId,
        [Description("New stage name")] string? stage = null,
        [Description("New amount in dollars")] decimal? amount = null)
    {
        var changes = new List<string>();
        if (stage  != null) changes.Add($"Stage → '{stage}'");
        if (amount != null) changes.Add($"Amount → ${amount:N0}");
        return $"[Opportunity Updated] {opportunityId}: {string.Join(", ", changes)}";
    }

    [Description("Create a new Salesforce opportunity linked to an account.")]
    public static string CreateOpportunity(
        [Description("Opportunity name")] string name,
        [Description("Account name")] string accountName,
        [Description("Stage name")] string stage,
        [Description("Expected close date YYYY-MM-DD")] string closeDate,
        [Description("Amount in dollars")] decimal amount)
    {
        return $"[Opportunity Created] '{name}' for {accountName} | {stage} | ${amount:N0} | Close: {closeDate}";
    }
}
