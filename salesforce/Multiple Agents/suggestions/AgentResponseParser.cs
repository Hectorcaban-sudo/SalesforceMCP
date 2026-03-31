using System.Text.RegularExpressions;

namespace ChatApp.Helpers;

/// <summary>
/// Parses the structured reply that every agent is instructed to produce:
///
///   … main answer text …
///   [SUGGESTIONS]suggestion one|suggestion two|suggestion three[/SUGGESTIONS]
///
/// If the agent omits the block (e.g. for error cases) the suggestions list
/// will be empty and the full raw text is returned as the answer.
/// </summary>
public static class AgentResponseParser
{
    private static readonly Regex SuggestionsRegex = new(
        @"\[SUGGESTIONS\](.*?)\[\/SUGGESTIONS\]",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedAgentResponse Parse(string raw)
    {
        var match = SuggestionsRegex.Match(raw);

        if (!match.Success)
        {
            return new ParsedAgentResponse
            {
                Answer      = raw.Trim(),
                Suggestions = []
            };
        }

        // Everything before the [SUGGESTIONS] tag is the answer
        var answer = raw[..match.Index].Trim();

        // Split the pipe-delimited suggestions, clean whitespace
        var suggestions = match.Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return new ParsedAgentResponse
        {
            Answer      = answer,
            Suggestions = suggestions
        };
    }
}

public sealed record ParsedAgentResponse
{
    public string       Answer      { get; init; } = string.Empty;
    public List<string> Suggestions { get; init; } = [];
}
