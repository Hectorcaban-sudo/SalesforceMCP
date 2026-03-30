using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatApp.Agents;

public static class SalesforceSoqlAgents
{
    public const string AccountsAgentName = "AccountsAgent";
    public const string OpportunitiesAgentName = "OpportunitiesAgent";
    public const string ContractsAgentName = "ContractsAgent";

    public static ChatCompletionAgent CreateAccountsAgent(Kernel kernel) =>
        new()
        {
            Name = AccountsAgentName,
            Description = "Salesforce Accounts expert that translates user requests into valid SOQL.",
            Kernel = kernel.Clone(),
            Instructions = """
                You are the Salesforce Accounts specialist.
                Convert account-related natural language requests into valid SOQL.

                Return JSON only:
                {
                  "agentName": "AccountsAgent",
                  "soql": "<valid SOQL>",
                  "explanation": "<short explanation>"
                }

                Use Salesforce Account fields only (Id, Name, Industry, Type, AnnualRevenue, BillingCity, BillingCountry, OwnerId, CreatedDate).
                If the user asks for unsupported fields, choose closest valid fields and explain.
                Never return markdown.
                """
        };

    public static ChatCompletionAgent CreateOpportunitiesAgent(Kernel kernel) =>
        new()
        {
            Name = OpportunitiesAgentName,
            Description = "Salesforce Opportunities expert that translates user requests into valid SOQL.",
            Kernel = kernel.Clone(),
            Instructions = """
                You are the Salesforce Opportunities specialist.
                Convert opportunity-related natural language requests into valid SOQL.

                Return JSON only:
                {
                  "agentName": "OpportunitiesAgent",
                  "soql": "<valid SOQL>",
                  "explanation": "<short explanation>"
                }

                Use Salesforce Opportunity fields only (Id, Name, StageName, Amount, CloseDate, Probability, ForecastCategoryName, AccountId, OwnerId, CreatedDate, IsClosed, IsWon).
                Include ORDER BY/LIMIT when user intent implies top/latest.
                Never return markdown.
                """
        };

    public static ChatCompletionAgent CreateContractsAgent(Kernel kernel) =>
        new()
        {
            Name = ContractsAgentName,
            Description = "Salesforce Contracts expert that translates user requests into valid SOQL.",
            Kernel = kernel.Clone(),
            Instructions = """
                You are the Salesforce Contracts specialist.
                Convert contract-related natural language requests into valid SOQL.

                Return JSON only:
                {
                  "agentName": "ContractsAgent",
                  "soql": "<valid SOQL>",
                  "explanation": "<short explanation>"
                }

                Use Salesforce Contract fields only (Id, ContractNumber, AccountId, Status, StartDate, EndDate, ContractTerm, OwnerId, CreatedDate, Description).
                For renewals/expiring requests, use EndDate filters.
                Never return markdown.
                """
        };
}
