using ChatApp.Plugins;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatApp.Agents;

/// <summary>
/// Builds the three Salesforce-specialized ChatCompletionAgents and wires them
/// into an AgentGroupChat for round-robin orchestration.
/// </summary>
public static class SalesforceAgentBuilder
{
    // ── Agent names ────────────────────────────────────────────────────────────
    public const string AccountsAgentName     = "AccountsAgent";
    public const string OpportunitiesAgentName = "OpportunitiesAgent";
    public const string ContractsAgentName    = "ContractsAgent";

    /// <summary>
    /// Creates a <see cref="ChatCompletionAgent"/> responsible for Salesforce Accounts.
    /// </summary>
    public static ChatCompletionAgent BuildAccountsAgent(Kernel kernel)
    {
        // Give this agent its own kernel clone with only the Accounts plugin
        var agentKernel = kernel.Clone();
        agentKernel.Plugins.AddFromObject(new AccountsPlugin(), "Accounts");

        return new ChatCompletionAgent
        {
            Name        = AccountsAgentName,
            Description = "Handles all Salesforce Account queries and mutations.",
            Instructions = """
                You are a Salesforce Account specialist.
                You help users query, create, and update Salesforce Accounts.
                Use the Accounts plugin functions to fetch or modify data.
                Always present account information in a clear, structured format.
                If a question is NOT about Accounts, respond with exactly: PASS
                """,
            Kernel      = agentKernel,
        };
    }

    /// <summary>
    /// Creates a <see cref="ChatCompletionAgent"/> responsible for Salesforce Opportunities.
    /// </summary>
    public static ChatCompletionAgent BuildOpportunitiesAgent(Kernel kernel)
    {
        var agentKernel = kernel.Clone();
        agentKernel.Plugins.AddFromObject(new OpportunitiesPlugin(), "Opportunities");

        return new ChatCompletionAgent
        {
            Name        = OpportunitiesAgentName,
            Description = "Handles all Salesforce Opportunity queries and pipeline management.",
            Instructions = """
                You are a Salesforce Opportunities specialist.
                You help users query pipeline, update deal stages, and create new opportunities.
                Use the Opportunities plugin functions to fetch or modify data.
                Summarise pipeline health and highlight at-risk deals when relevant.
                If a question is NOT about Opportunities, respond with exactly: PASS
                """,
            Kernel      = agentKernel,
        };
    }

    /// <summary>
    /// Creates a <see cref="ChatCompletionAgent"/> responsible for Salesforce Contracts.
    /// </summary>
    public static ChatCompletionAgent BuildContractsAgent(Kernel kernel)
    {
        var agentKernel = kernel.Clone();
        agentKernel.Plugins.AddFromObject(new ContractsPlugin(), "Contracts");

        return new ChatCompletionAgent
        {
            Name        = ContractsAgentName,
            Description = "Handles all Salesforce Contract queries, activations, and renewals.",
            Instructions = """
                You are a Salesforce Contracts specialist.
                You help users review contract status, activate drafts, flag expiring contracts,
                and create new contracts.
                Use the Contracts plugin functions to fetch or modify data.
                Always highlight contracts expiring within 90 days.
                If a question is NOT about Contracts, respond with exactly: PASS
                """,
            Kernel      = agentKernel,
        };
    }

    /// <summary>
    /// Assembles all three agents into an <see cref="AgentGroupChat"/>.
    /// The group chat uses a <see cref="KernelFunctionSelectionStrategy"/> so the
    /// kernel itself picks which agent should respond based on the user's message.
    /// </summary>
    public static AgentGroupChat BuildGroupChat(Kernel kernel)
    {
        var accountsAgent     = BuildAccountsAgent(kernel);
        var opportunitiesAgent = BuildOpportunitiesAgent(kernel);
        var contractsAgent    = BuildContractsAgent(kernel);

        // Selection strategy — a prompt-based function that picks the right agent
        var selectionFunction = KernelFunctionFactory.CreateFromPrompt(
            promptTemplate: """
                You are a router. Given the conversation history below, decide which agent
                should respond next. Reply with ONLY the agent name — nothing else.

                Agents:
                - AccountsAgent      → Salesforce accounts, companies, customers
                - OpportunitiesAgent → deals, pipeline, stages, revenue, forecast
                - ContractsAgent     → contracts, agreements, renewals, expiry

                History:
                {{$history}}

                Agent name:
                """,
            functionName: "SelectAgent");

        var groupChat = new AgentGroupChat(accountsAgent, opportunitiesAgent, contractsAgent)
        {
            ExecutionSettings = new AgentGroupChatSettings
            {
                SelectionStrategy = new KernelFunctionSelectionStrategy(selectionFunction, kernel)
                {
                    // Map the LLM's text output back to an agent instance
                    AgentsVariableName  = "agents",
                    HistoryVariableName = "history",
                },
                // One agent responds per user turn
                TerminationStrategy = new MaxTurnTerminationStrategy(maxTurns: 1),
            }
        };

        return groupChat;
    }
}
