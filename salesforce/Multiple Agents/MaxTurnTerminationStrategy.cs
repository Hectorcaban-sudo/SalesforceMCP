using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;

namespace ChatApp.Agents;

/// <summary>
/// Terminates the AgentGroupChat after a fixed number of agent turns.
/// For a simple request/response pattern, maxTurns = 1 means exactly one
/// agent replies per user message.
/// </summary>
public sealed class MaxTurnTerminationStrategy(int maxTurns = 1) : TerminationStrategy
{
    private int _turns;

    protected override Task<bool> ShouldAgentTerminateAsync(
        Agent agent, IReadOnlyList<ChatMessageContent> history, CancellationToken ct)
    {
        _turns++;
        var shouldStop = _turns >= maxTurns;
        if (shouldStop) _turns = 0;   // reset for next request
        return Task.FromResult(shouldStop);
    }
}
