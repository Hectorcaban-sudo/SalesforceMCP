using Microsoft.SemanticKernel;

namespace ChatApp.Plugins;

public sealed class AccountsPlugin
{
    [KernelFunction]
    public string GetObjectName() => "Account";

    [KernelFunction]
    public string GetQueryableFields() =>
        "Id, Name, Industry, Type, AnnualRevenue, BillingCity, BillingCountry, OwnerId, CreatedDate";

    [KernelFunction]
    public string ExecuteSoql(string soql)
    {
        // Placeholder to show where Salesforce REST/Bulk execution would occur.
        return $"Accounts plugin accepted SOQL for execution: {soql}";
    }
}
