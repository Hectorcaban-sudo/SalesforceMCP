using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace ChatApp.Agents;

/// <summary>
/// Custom GroupChatManager that uses the OpenAI ChatClient to route each
/// user message to the correct Salesforce specialist agent.
/// Terminates after one agent has replied.
/// </summary>
public sealed class SalesforceGroupChatManager(
    IReadOnlyList<AIAgent> agents,
    ChatClient chatClient)
    : RoundRobinGroupChatManager(agents)
{
    // ── LLM-based agent selection ─────────────────────────────────────────────
    public override async ValueTask<string> SelectNextSpeakerAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text
                       ?? string.Empty;

        var agentList = string.Join("\n", agents.Select(a => $"- {a.Name}"));

        var routingPrompt = $"""
            You are a router. Based on the user message, pick the correct Salesforce specialist.
            Reply with ONLY the agent name — nothing else.

            Agents:
            {agentList}

            Routing rules:
            - AccountsAgent      → accounts, companies, customers, contacts
            - OpportunitiesAgent → deals, pipeline, stages, revenue, forecast, won/lost
            - ContractsAgent     → contracts, agreements, renewals, expiry, activation

            User message: "{lastUser}"

            Agent name:
            """;

        // Call the internal OpenAPI server directly via OpenAI ChatClient
        var oaiMessages = new List<OpenAI.Chat.ChatMessage>
        {
            OpenAI.Chat.ChatMessage.CreateUserMessage(routingPrompt)
        };

        var response  = await chatClient.CompleteChatAsync(oaiMessages, cancellationToken: cancellationToken);
        var agentName = response.Value.Content[0].Text?.Trim() ?? string.Empty;

        var matched = agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));

        return (matched ?? agents[0]).Name!;
    }

    // ── Terminate after first assistant reply ─────────────────────────────────
    protected override ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var agentReplies = history.Count(m => m.Role == ChatRole.Assistant);
        return ValueTask.FromResult(agentReplies >= 1);
    }
}
