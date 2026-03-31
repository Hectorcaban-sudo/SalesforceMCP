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
///
/// Each agent is instructed to end every reply with a SUGGESTIONS block —
/// a pipe-separated list of follow-up prompts the user can tap next.
/// Format (agents must follow this exactly):
///
///   [ANSWER]
///   … main response …
///   [/ANSWER]
///   [SUGGESTIONS]prompt one|prompt two|prompt three[/SUGGESTIONS]
/// </summary>
public static class SalesforceAgentBuilder
{
    public const string AccountsAgentName      = "AccountsAgent";
    public const string OpportunitiesAgentName = "OpportunitiesAgent";
    public const string ContractsAgentName     = "ContractsAgent";

    private const string SuggestionFormat = """

        After your answer, you MUST append suggestions the user might want to ask next.
        Use this exact format on a new line — no deviations:
        [SUGGESTIONS]suggestion one|suggestion two|suggestion three[/SUGGESTIONS]

        Keep each suggestion short (under 8 words). Make them specific to the data you just returned.
        Always provide exactly 3 suggestions.
        """;

    // ── Individual agent factories ─────────────────────────────────────────
    public static ChatCompletionAgent BuildAccountsAgent(Kernel kernel)
    {
        var k = kernel.Clone();
        k.Plugins.AddFromObject(new AccountsPlugin(), "Accounts");
        return new ChatCompletionAgent
        {
            Name        = AccountsAgentName,
            Description = "Handles Salesforce Accounts — query, create, update.",
            Instructions = $"""
                You are a Salesforce Accounts specialist.
                Use the Accounts plugin to fetch or modify account data.
                Present results in a clear, structured format.
                Only handle questions about Accounts.
                {SuggestionFormat}
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
            Name        = OpportunitiesAgentName,
            Description = "Handles Salesforce Opportunities — pipeline, stages, forecast.",
            Instructions = $"""
                You are a Salesforce Opportunities specialist.
                Use the Opportunities plugin to fetch or modify pipeline data.
                Highlight at-risk deals and forecast health where relevant.
                Only handle questions about Opportunities.
                {SuggestionFormat}
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
            Name        = ContractsAgentName,
            Description = "Handles Salesforce Contracts — status, activation, renewals.",
            Instructions = $"""
                You are a Salesforce Contracts specialist.
                Use the Contracts plugin to fetch or modify contract data.
                Always flag contracts expiring within 90 days.
                Only handle questions about Contracts.
                {SuggestionFormat}
                """,
            Kernel = k,
        };
    }

    /// <summary>
    /// Builds a <see cref="GroupChatOrchestration"/> containing all three agents.
    /// </summary>
    public static GroupChatOrchestration BuildOrchestration(
        Kernel kernel,
        IList<ChatMessageContent> responseBuffer)
    {
        var manager = new SalesforceGroupChatManager(kernel);

        return new GroupChatOrchestration(
            manager,
            BuildAccountsAgent(kernel),
            BuildOpportunitiesAgent(kernel),
            BuildContractsAgent(kernel))
        {
            ResponseCallback = msg =>
            {
                responseBuffer.Add(msg);
                return ValueTask.CompletedTask;
            }
        };
    }
}
