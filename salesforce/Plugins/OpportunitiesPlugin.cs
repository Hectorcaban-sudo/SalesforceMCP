using Microsoft.SemanticKernel;

namespace ChatApp.Plugins;

public sealed class OpportunitiesPlugin
{
    [KernelFunction]
    public string GetObjectName() => "Opportunity";

    [KernelFunction]
    public string GetQueryableFields() =>
        "Id, Name, StageName, Amount, CloseDate, Probability, ForecastCategoryName, AccountId, OwnerId, CreatedDate, IsClosed, IsWon";

    [KernelFunction]
    public string ExecuteSoql(string soql)
    {
        // Placeholder to show where Salesforce REST/Bulk execution would occur.
        return $"Opportunities plugin accepted SOQL for execution: {soql}";
    }
}
