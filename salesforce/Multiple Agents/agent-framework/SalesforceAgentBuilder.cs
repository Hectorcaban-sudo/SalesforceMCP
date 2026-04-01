using ChatApp.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using OpenAI.Chat;

namespace ChatApp.Agents;

/// <summary>
/// Builds the three Salesforce AIAgents and wires them into a GroupChat workflow.
/// Agents are created directly from <see cref="ChatClient"/> via .AsAIAgent() —
/// no IChatClient wrapper needed.
/// </summary>
public static class SalesforceAgentBuilder
{
    private const string SuggestionFormat = """

        After your answer, append exactly this block — no deviations:
        [SUGGESTIONS]suggestion one|suggestion two|suggestion three[/SUGGESTIONS]
        Keep each suggestion under 8 words and specific to the data you returned.
        """;

    // ── Individual agent factories ─────────────────────────────────────────
    public static AIAgent BuildAccountsAgent(ChatClient chatClient) =>
        chatClient.AsAIAgent(
            instructions: $"""
                You are a Salesforce Accounts specialist.
                Use the provided tools to fetch or modify account data.
                Present results in a clear, structured format.
                Only handle questions about Salesforce Accounts.
                {SuggestionFormat}
                """,
            name: "AccountsAgent",
            tools:
            [
                AIFunctionFactory.Create(AccountsTools.GetAccounts),
                AIFunctionFactory.Create(AccountsTools.GetAccountDetail),
                AIFunctionFactory.Create(AccountsTools.CreateAccount),
                AIFunctionFactory.Create(AccountsTools.UpdateAccount),
            ]);

    public static AIAgent BuildOpportunitiesAgent(ChatClient chatClient) =>
        chatClient.AsAIAgent(
            instructions: $"""
                You are a Salesforce Opportunities specialist.
                Use the provided tools to fetch or modify pipeline data.
                Highlight at-risk deals and forecast health where relevant.
                Only handle questions about Salesforce Opportunities.
                {SuggestionFormat}
                """,
            name: "OpportunitiesAgent",
            tools:
            [
                AIFunctionFactory.Create(OpportunitiesTools.GetOpportunities),
                AIFunctionFactory.Create(OpportunitiesTools.GetOpportunityDetail),
                AIFunctionFactory.Create(OpportunitiesTools.UpdateOpportunity),
                AIFunctionFactory.Create(OpportunitiesTools.CreateOpportunity),
            ]);

    public static AIAgent BuildContractsAgent(ChatClient chatClient) =>
        chatClient.AsAIAgent(
            instructions: $"""
                You are a Salesforce Contracts specialist.
                Use the provided tools to fetch or modify contract data.
                Always flag contracts expiring within 90 days.
                Only handle questions about Salesforce Contracts.
                {SuggestionFormat}
                """,
            name: "ContractsAgent",
            tools:
            [
                AIFunctionFactory.Create(ContractsTools.GetContracts),
                AIFunctionFactory.Create(ContractsTools.GetContractDetail),
                AIFunctionFactory.Create(ContractsTools.ActivateContract),
                AIFunctionFactory.Create(ContractsTools.CreateContract),
                AIFunctionFactory.Create(ContractsTools.GetExpiringContracts),
            ]);

    /// <summary>
    /// Assembles all three agents into a GroupChat <see cref="Workflow"/>.
    /// </summary>
    public static Workflow BuildGroupChatWorkflow(ChatClient chatClient) =>
        AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
                new SalesforceGroupChatManager(agents, chatClient)
                {
                    MaximumIterationCount = 1
                })
            .AddParticipants(
                BuildAccountsAgent(chatClient),
                BuildOpportunitiesAgent(chatClient),
                BuildContractsAgent(chatClient))
            .Build();
}
