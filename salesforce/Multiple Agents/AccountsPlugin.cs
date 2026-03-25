using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ChatApp.Plugins;

/// <summary>
/// Simulated Salesforce Accounts plugin.
/// In production, replace method bodies with real Salesforce API / SOQL calls.
/// </summary>
public sealed class AccountsPlugin
{
    [KernelFunction, Description("Get a list of Salesforce accounts, optionally filtered by name or industry.")]
    public string GetAccounts(
        [Description("Optional partial account name to search for")] string? name = null,
        [Description("Optional industry filter e.g. Technology, Healthcare")] string? industry = null)
    {
        // TODO: replace with SOQL: SELECT Id, Name, Industry, AnnualRevenue FROM Account WHERE ...
        return $"""
            [Accounts]
            - Acme Corp | Technology | $4.2M ARR
            - Globex Inc | Manufacturing | $1.8M ARR
            - Initech LLC | Financial Services | $3.1M ARR
            Filter applied → name: '{name ?? "any"}', industry: '{industry ?? "any"}'
            """;
    }

    [KernelFunction, Description("Get detailed information about a specific Salesforce account by name or ID.")]
    public string GetAccountDetail(
        [Description("Account name or Salesforce Account ID")] string accountId)
    {
        // TODO: SELECT Id, Name, Phone, BillingCity, OwnerId FROM Account WHERE Id = :accountId
        return $"""
            [Account Detail: {accountId}]
            Name:        Acme Corp
            Industry:    Technology
            Phone:       +1 415-555-0100
            Billing City: San Francisco
            Owner:       Jane Smith
            ARR:         $4.2M
            Health:      Green
            """;
    }

    [KernelFunction, Description("Create a new Salesforce account.")]
    public string CreateAccount(
        [Description("Account name")] string name,
        [Description("Industry")] string industry,
        [Description("Phone number")] string? phone = null)
    {
        // TODO: INSERT into Account
        return $"[Account Created] Name: {name}, Industry: {industry}, Phone: {phone ?? "N/A"} — ID: ACC-{Guid.NewGuid():N[..8]}";
    }

    [KernelFunction, Description("Update an existing Salesforce account field.")]
    public string UpdateAccount(
        [Description("Account name or ID")] string accountId,
        [Description("Field to update e.g. Phone, BillingCity")] string field,
        [Description("New value")] string value)
    {
        // TODO: UPDATE Account SET {field} = {value} WHERE Id = :accountId
        return $"[Account Updated] {accountId} → {field} set to '{value}'";
    }
}
