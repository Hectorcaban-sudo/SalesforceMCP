using System.ComponentModel;

namespace ChatApp.Plugins;

/// <summary>
/// Salesforce Contracts tools — registered with AIFunctionFactory.Create().
/// </summary>
public static class ContractsTools
{
    [Description("Get a list of Salesforce contracts, optionally filtered by status or account.")]
    public static string GetContracts(
        [Description("Status filter: Draft, Activated, Expired")] string? status = null,
        [Description("Account name filter")] string? accountName = null)
    {
        return $"""
            [Contracts]
            - CTR-0001 | Acme Corp   | Activated | 2024-01-01 → 2025-12-31 | $96,000/yr
            - CTR-0002 | Globex Inc  | Draft     | 2025-07-01 → 2026-06-30 | $45,000/yr
            - CTR-0003 | Initech LLC | Activated | 2023-06-01 → 2025-05-31 | $180,000/yr  ← EXPIRING SOON
            - CTR-0004 | Acme Corp   | Expired   | 2022-01-01 → 2023-12-31 | $72,000/yr
            Filter → status: '{status ?? "any"}', account: '{accountName ?? "any"}'
            """;
    }

    [Description("Get details of a specific Salesforce contract by contract number or ID.")]
    public static string GetContractDetail(
        [Description("Contract number or Salesforce Contract ID")] string contractId)
    {
        return $"""
            [Contract: {contractId}]
            Account: Acme Corp | Status: Activated | Auto-Renew: Yes
            Period: 2024-01-01 → 2025-12-31 | ACV: $96,000 | Terms: Net 30
            """;
    }

    [Description("Activate a draft Salesforce contract.")]
    public static string ActivateContract(
        [Description("Contract number or ID to activate")] string contractId)
    {
        return $"[Contract Activated] {contractId} is now Active.";
    }

    [Description("Create a new Salesforce contract linked to an account.")]
    public static string CreateContract(
        [Description("Account name")] string accountName,
        [Description("Contract start date YYYY-MM-DD")] string startDate,
        [Description("Term in months")] int termMonths,
        [Description("Annual contract value in dollars")] decimal annualValue)
    {
        return $"[Contract Created] {accountName} | Start: {startDate} | {termMonths}mo | ACV: ${annualValue:N0}";
    }

    [Description("List contracts expiring within the next N days.")]
    public static string GetExpiringContracts(
        [Description("Number of days to look ahead (default 90)")] int days = 90)
    {
        return $"""
            [Expiring within {days} days]
            - CTR-0003 | Initech LLC | Expires: 2025-05-31 | $180,000/yr  ← URGENT
            """;
    }
}
