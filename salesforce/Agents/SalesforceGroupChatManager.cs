using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatApp.Agents;

public sealed class SalesforceGroupChatManager(Kernel kernel) : GroupChatManager
{
    public override async ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        var latestUser = history.LastOrDefault(m => m.Role == AuthorRole.User)?.Content ?? string.Empty;

        var prompt = new ChatHistory();
        prompt.AddUserMessage($$"""
            You are a strict Salesforce routing controller.
            Choose one agent name only from:
            - {{SalesforceSoqlAgents.AccountsAgentName}}
            - {{SalesforceSoqlAgents.OpportunitiesAgentName}}
            - {{SalesforceSoqlAgents.ContractsAgentName}}

            Routing hints:
            - Accounts: account, company, customer, billing account details.
            - Opportunities: opportunity, pipeline, stage, forecast, deals, revenue.
            - Contracts: contract, agreement, renewal, expiration, term.

            User request: "{{latestUser}}"

            Return only the agent name.
            """);

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var selectedName = (await chatService.GetChatMessageContentAsync(prompt, cancellationToken: cancellationToken))
            .Content?.Trim();

        var match = team.Members.FirstOrDefault(a =>
            string.Equals(a.Name, selectedName, StringComparison.OrdinalIgnoreCase));

        var agent = match ?? team.Members.First();

        return new GroupChatManagerResult<string>(agent.Name ?? team.Members.First().Name!)
        {
            Reason = "Routed by domain intent."
        };
    }

    public override ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        var hasAgentReply = history.Any(m => m.Role == AuthorRole.Assistant);

        return ValueTask.FromResult(new GroupChatManagerResult<bool>(hasAgentReply)
        {
            Reason = hasAgentReply ? "Received one domain agent answer." : "Awaiting first domain agent answer."
        });
    }

    public override ValueTask<GroupChatManagerResult<string>> FilterResults(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        var last = history.LastOrDefault(m => m.Role == AuthorRole.Assistant)?.Content ?? string.Empty;

        return ValueTask.FromResult(new GroupChatManagerResult<string>(last)
        {
            Reason = "Returning the selected agent SOQL response."
        });
    }

    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(
        ChatHistory history,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new GroupChatManagerResult<bool>(false));
}
