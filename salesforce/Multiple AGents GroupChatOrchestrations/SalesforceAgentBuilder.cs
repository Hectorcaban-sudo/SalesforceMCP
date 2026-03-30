using ChatApp.Plugins;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatApp.Agents;

/// <summary>
/// Builds the three Salesforce-specialised ChatCompletionAgents and wires them
/// into a GroupChatOrchestration with a custom LLM-based selection manager.
/// </summary>
public static class SalesforceAgentBuilder
{
    public const string AccountsAgentName      = "AccountsAgent";
    public const string OpportunitiesAgentName = "OpportunitiesAgent";
    public const string ContractsAgentName     = "ContractsAgent";

    // ── Individual agent factories ─────────────────────────────────────────
    public static ChatCompletionAgent BuildAccountsAgent(Kernel kernel)
    {
        var k = kernel.Clone();
        k.Plugins.AddFromObject(new AccountsPlugin(), "Accounts");
        return new ChatCompletionAgent
        {
            Name         = AccountsAgentName,
            Description  = "Handles Salesforce Accounts — query, create, update.",
            Instructions = """
                You are a Salesforce Accounts specialist.
                Use the Accounts plugin to fetch or modify account data.
                Present results in a clear, structured format.
                Only handle questions about Accounts.
                """,
            Kernel = k,
        };
    }

    public static ChatCompletionAgent BuildOpportunitiesAgent(Kernel kernel)
    {
        var k = kernel.Clone();
        k.Plugins.AddFromObject(new OpportunitiesPlugin(), "Opportunities");
        return new ChatCompletionAgent
        {
            Name         = OpportunitiesAgentName,
            Description  = "Handles Salesforce Opportunities — pipeline, stages, forecast.",
            Instructions = """
                You are a Salesforce Opportunities specialist.
                Use the Opportunities plugin to fetch or modify pipeline data.
                Highlight at-risk deals and forecast health where relevant.
                Only handle questions about Opportunities.
                """,
            Kernel = k,
        };
    }

    public static ChatCompletionAgent BuildContractsAgent(Kernel kernel)
    {
        var k = kernel.Clone();
        k.Plugins.AddFromObject(new ContractsPlugin(), "Contracts");
        return new ChatCompletionAgent
        {
            Name         = ContractsAgentName,
            Description  = "Handles Salesforce Contracts — status, activation, renewals.",
            Instructions = """
                You are a Salesforce Contracts specialist.
                Use the Contracts plugin to fetch or modify contract data.
                Always flag contracts expiring within 90 days.
                Only handle questions about Contracts.
                """,
            Kernel = k,
        };
    }

    /// <summary>
    /// Builds a <see cref="GroupChatOrchestration"/> containing all three agents,
    /// managed by a <see cref="SalesforceGroupChatManager"/> that uses the Kernel
    /// to select the right agent for each user message.
    /// </summary>
    public static GroupChatOrchestration BuildOrchestration(
        Kernel kernel,
        IList<ChatMessageContent> responseBuffer)
    {
        var accountsAgent      = BuildAccountsAgent(kernel);
        var opportunitiesAgent = BuildOpportunitiesAgent(kernel);
        var contractsAgent     = BuildContractsAgent(kernel);

        // Custom manager — LLM-based agent selection, terminates after 1 agent reply
        var manager = new SalesforceGroupChatManager(kernel);

        return new GroupChatOrchestration(
            manager,
            accountsAgent,
            opportunitiesAgent,
            contractsAgent)
        {
            // Capture every agent response into the shared buffer
            ResponseCallback = msg =>
            {
                responseBuffer.Add(msg);
                return ValueTask.CompletedTask;
            }
        };
    }
}
