using Microsoft.SemanticKernel;

namespace ChatApp.Plugins;

public sealed class ContractsPlugin
{
    [KernelFunction]
    public string GetObjectName() => "Contract";

    [KernelFunction]
    public string GetQueryableFields() =>
        "Id, ContractNumber, AccountId, Status, StartDate, EndDate, ContractTerm, OwnerId, CreatedDate, Description";

    [KernelFunction]
    public string ExecuteSoql(string soql)
    {
        // Placeholder to show where Salesforce REST/Bulk execution would occur.
        return $"Contracts plugin accepted SOQL for execution: {soql}";
    }
}
