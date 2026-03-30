using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatApp.Agents;

/// <summary>
/// Custom <see cref="GroupChatManager"/> that:
/// 1. Uses the Kernel (LLM) to pick which agent should respond next.
/// 2. Terminates after exactly one agent has replied.
/// 3. Returns that agent's reply as the final result.
/// </summary>
public sealed class SalesforceGroupChatManager(Kernel kernel) : GroupChatManager
{
    private int _agentReplies;

    // ── Select which agent replies next ───────────────────────────────────────
    public override async ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        // Build a routing prompt from the conversation so far
        var lastUserMsg = history.LastOrDefault(m => m.Role == AuthorRole.User)?.Content
                          ?? string.Empty;

        var routingPrompt = $"""
            You are a router. Based on the user's message, decide which Salesforce specialist agent should respond.
            Reply with ONLY the agent name — nothing else.

            Agents:
            - AccountsAgent      → accounts, companies, customers, contacts
            - OpportunitiesAgent → deals, pipeline, stages, revenue, forecast, won/lost
            - ContractsAgent     → contracts, agreements, renewals, expiry, activation

            User message: "{lastUserMsg}"

            Agent name:
            """;

        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var prompt      = new ChatHistory();
        prompt.AddUserMessage(routingPrompt);

        var response   = await chatService.GetChatMessageContentAsync(prompt, cancellationToken: cancellationToken);
        var agentName  = response.Content?.Trim() ?? team.Members.First().Name;

        // Fall back to first agent if LLM returned an unrecognised name
        var matched = team.Members.FirstOrDefault(m =>
            string.Equals(m.Name, agentName, StringComparison.OrdinalIgnoreCase));

        var selected = matched?.Name ?? team.Members.First().Name!;

        return new GroupChatManagerResult<string>(selected)
        {
            Reason = $"Routed to {selected} based on user message topic."
        };
    }

    // ── Decide whether to terminate ───────────────────────────────────────────
    public override ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        // Count assistant (agent) replies added since the last user message
        _agentReplies = history.Count(m => m.Role == AuthorRole.Assistant);

        // Stop after the first agent reply
        var shouldStop = _agentReplies >= 1;

        return ValueTask.FromResult(new GroupChatManagerResult<bool>(shouldStop)
        {
            Reason = shouldStop ? "One agent has responded — terminating." : "Waiting for agent response."
        });
    }

    // ── Filter / summarise the final result ───────────────────────────────────
    public override ValueTask<GroupChatManagerResult<string>> FilterResults(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        // Return the last assistant message as the orchestration's final value
        var lastAssistant = history.LastOrDefault(m => m.Role == AuthorRole.Assistant);
        var result        = lastAssistant?.Content ?? string.Empty;

        return ValueTask.FromResult(new GroupChatManagerResult<string>(result)
        {
            Reason = "Returning last agent response."
        });
    }

    // ── Human input — not used in this scenario ───────────────────────────────
    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(
        ChatHistory history,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
        {
            Reason = "No human-in-the-loop required."
        });
}
