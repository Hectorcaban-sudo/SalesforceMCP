using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ChatApp.Plugins;

/// <summary>
/// Simulated Salesforce Contracts plugin.
/// In production, replace method bodies with real Salesforce API / SOQL calls.
/// </summary>
public sealed class ContractsPlugin
{
    [KernelFunction, Description("Get a list of Salesforce contracts, optionally filtered by status or account.")]
    public string GetContracts(
        [Description("Optional status filter: Draft, Activated, Expired")] string? status = null,
        [Description("Optional account name to filter by")] string? accountName = null)
    {
        // TODO: SELECT Id, ContractNumber, Status, StartDate, EndDate, AccountId FROM Contract WHERE ...
        return $"""
            [Contracts]
            - CTR-0001 | Acme Corp      | Activated | Start: 2024-01-01 | End: 2025-12-31 | $96,000/yr
            - CTR-0002 | Globex Inc     | Draft     | Start: 2025-07-01 | End: 2026-06-30 | $45,000/yr
            - CTR-0003 | Initech LLC    | Activated | Start: 2023-06-01 | End: 2025-05-31 | $180,000/yr
            - CTR-0004 | Acme Corp      | Expired   | Start: 2022-01-01 | End: 2023-12-31 | $72,000/yr
            Filter applied → status: '{status ?? "any"}', account: '{accountName ?? "any"}'
            """;
    }

    [KernelFunction, Description("Get details of a specific Salesforce contract.")]
    public string GetContractDetail(
        [Description("Contract number or Salesforce Contract ID")] string contractId)
    {
        // TODO: SELECT * FROM Contract WHERE Id = :contractId
        return $"""
            [Contract Detail: {contractId}]
            Contract #:  CTR-0001
            Account:     Acme Corp
            Status:      Activated
            Start Date:  2024-01-01
            End Date:    2025-12-31
            Value:       $96,000/yr
            Terms:       Net 30
            Owner:       Legal Team
            Auto-Renew:  Yes
            """;
    }

    [KernelFunction, Description("Activate a draft Salesforce contract.")]
    public string ActivateContract(
        [Description("Contract number or ID to activate")] string contractId)
    {
        // TODO: UPDATE Contract SET Status = 'Activated' WHERE Id = :contractId
        return $"[Contract Activated] {contractId} is now Active.";
    }

    [KernelFunction, Description("Create a new Salesforce contract linked to an account.")]
    public string CreateContract(
        [Description("Account name")] string accountName,
        [Description("Contract start date (YYYY-MM-DD)")] string startDate,
        [Description("Contract term in months")] int termMonths,
        [Description("Annual contract value in dollars")] decimal annualValue)
    {
        // TODO: INSERT into Contract
        return $"[Contract Created] Account: {accountName} | Start: {startDate} | Term: {termMonths}mo | ACV: ${annualValue:N0} — ID: CTR-{Guid.NewGuid():N[..8]}";
    }

    [KernelFunction, Description("Check if any contracts are expiring within a given number of days.")]
    public string GetExpiringContracts(
        [Description("Number of days to look ahead")] int days = 90)
    {
        // TODO: SELECT * FROM Contract WHERE Status = 'Activated' AND EndDate <= :cutoffDate
        return $"""
            [Expiring Contracts within {days} days]
            - CTR-0003 | Initech LLC | Expires: 2025-05-31 | $180,000/yr  ← URGENT
            """;
    }
}
