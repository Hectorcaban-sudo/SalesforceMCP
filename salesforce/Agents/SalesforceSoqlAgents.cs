using ChatApp.Plugins;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatApp.Agents;

public static class SalesforceSoqlAgents
{
    public const string AccountsAgentName = "AccountsAgent";
    public const string OpportunitiesAgentName = "OpportunitiesAgent";
    public const string ContractsAgentName = "ContractsAgent";

    public static ChatCompletionAgent CreateAccountsAgent(Kernel kernel)
    {
        var domainKernel = kernel.Clone();
        domainKernel.Plugins.AddFromObject(new AccountsPlugin(), "AccountsPlugin");

        return new ChatCompletionAgent
        {
            Name = AccountsAgentName,
            Description = "Salesforce Accounts expert that translates user requests into valid SOQL.",
            Kernel = domainKernel,
            Instructions = """
                You are the Salesforce Accounts specialist.
                Convert account-related natural language requests into valid SOQL.

                Use AccountsPlugin.GetObjectName and AccountsPlugin.GetQueryableFields before producing a query.
                Optionally call AccountsPlugin.ExecuteSoql after generating SOQL to stage execution.

                Return JSON only:
                {
                  "agentName": "AccountsAgent",
                  "soql": "<valid SOQL>",
                  "explanation": "<short explanation>"
                }

                If the user asks for unsupported fields, choose the closest supported fields and explain briefly.
                Never return markdown.
                """
        };
    }

    public static ChatCompletionAgent CreateOpportunitiesAgent(Kernel kernel)
    {
        var domainKernel = kernel.Clone();
        domainKernel.Plugins.AddFromObject(new OpportunitiesPlugin(), "OpportunitiesPlugin");

        return new ChatCompletionAgent
        {
            Name = OpportunitiesAgentName,
            Description = "Salesforce Opportunities expert that translates user requests into valid SOQL.",
            Kernel = domainKernel,
            Instructions = """
                You are the Salesforce Opportunities specialist.
                Convert opportunity-related natural language requests into valid SOQL.

                Use OpportunitiesPlugin.GetObjectName and OpportunitiesPlugin.GetQueryableFields before producing a query.
                Optionally call OpportunitiesPlugin.ExecuteSoql after generating SOQL to stage execution.

                Return JSON only:
                {
                  "agentName": "OpportunitiesAgent",
                  "soql": "<valid SOQL>",
                  "explanation": "<short explanation>"
                }

                Include ORDER BY/LIMIT when user intent implies top/latest.
                Never return markdown.
                """
        };
    }

    public static ChatCompletionAgent CreateContractsAgent(Kernel kernel)
    {
        var domainKernel = kernel.Clone();
        domainKernel.Plugins.AddFromObject(new ContractsPlugin(), "ContractsPlugin");

        return new ChatCompletionAgent
        {
            Name = ContractsAgentName,
            Description = "Salesforce Contracts expert that translates user requests into valid SOQL.",
            Kernel = domainKernel,
            Instructions = """
                You are the Salesforce Contracts specialist.
                Convert contract-related natural language requests into valid SOQL.

                Use ContractsPlugin.GetObjectName and ContractsPlugin.GetQueryableFields before producing a query.
                Optionally call ContractsPlugin.ExecuteSoql after generating SOQL to stage execution.

                Return JSON only:
                {
                  "agentName": "ContractsAgent",
                  "soql": "<valid SOQL>",
                  "explanation": "<short explanation>"
                }

                For renewals/expiring requests, prefer EndDate filters.
                Never return markdown.
                """
        };
    }
}
