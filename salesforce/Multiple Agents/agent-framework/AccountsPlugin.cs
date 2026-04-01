using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace ChatApp.Plugins;

/// <summary>
/// Salesforce Accounts tools — registered with AIFunctionFactory.Create().
/// Replace method bodies with real Salesforce REST / SOQL calls.
/// </summary>
public static class AccountsTools
{
    [Description("Get a list of Salesforce accounts, optionally filtered by name or industry.")]
    public static string GetAccounts(
        [Description("Optional partial account name")] string? name = null,
        [Description("Optional industry filter e.g. Technology, Healthcare")] string? industry = null)
    {
        // TODO: SOQL SELECT Id, Name, Industry, AnnualRevenue FROM Account WHERE ...
        return $"""
            [Accounts]
            - Acme Corp | Technology | $4.2M ARR
            - Globex Inc | Manufacturing | $1.8M ARR
            - Initech LLC | Financial Services | $3.1M ARR
            Filter → name: '{name ?? "any"}', industry: '{industry ?? "any"}'
            """;
    }

    [Description("Get details of a specific Salesforce account by name or ID.")]
    public static string GetAccountDetail(
        [Description("Account name or Salesforce ID")] string accountId)
    {
        // TODO: SELECT * FROM Account WHERE Id = :accountId
        return $"""
            [Account: {accountId}]
            Name: Acme Corp | Industry: Technology | ARR: $4.2M
            Owner: Jane Smith | Phone: +1 415-555-0100 | Health: Green
            """;
    }

    [Description("Create a new Salesforce account.")]
    public static string CreateAccount(
        [Description("Account name")] string name,
        [Description("Industry")] string industry,
        [Description("Phone number")] string? phone = null)
    {
        // TODO: INSERT into Account
        return $"[Account Created] {name} | {industry} | {phone ?? "no phone"} — ID: ACC-{Guid.NewGuid():N[..8]}";
    }

    [Description("Update a field on an existing Salesforce account.")]
    public static string UpdateAccount(
        [Description("Account name or ID")] string accountId,
        [Description("Field to update e.g. Phone, BillingCity")] string field,
        [Description("New value")] string value)
    {
        // TODO: UPDATE Account SET :field = :value WHERE Id = :accountId
        return $"[Account Updated] {accountId} → {field} = '{value}'";
    }
}
