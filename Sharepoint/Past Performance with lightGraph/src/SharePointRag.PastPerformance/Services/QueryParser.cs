using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Prompts;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Sends the user's raw question to GPT-4o and extracts a structured
/// <see cref="PastPerformanceQuery"/> with intent, filters, and a
/// dense semantic-search phrase ready for HNSW lookup.
/// </summary>
public sealed class LlmQueryParser(
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    ILogger<LlmQueryParser> logger) : IQueryParser
{
    private readonly AzureOpenAIOptions _aoai = aoaiOpts.Value;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task<PastPerformanceQuery> ParseAsync(string rawQuestion, CancellationToken ct = default)
    {
        logger.LogDebug("Parsing query intent for: {Q}", rawQuestion);

        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.QueryParserSystem),
            new UserChatMessage(
                PastPerformancePrompts.QueryParserUserTemplate
                    .Replace("{question}", rawQuestion))
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 512,
            Temperature         = 0.0f   // deterministic for structured output
        };

        var response = await client.CompleteChatAsync(messages, options, ct);
        var json     = response.Value.Content[0].Text.Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<PastPerformanceQuery>(json, _jsonOpts)
                         ?? new PastPerformanceQuery { RawQuestion = rawQuestion, SemanticQuery = rawQuestion };

            // Ensure raw question is always stored
            return parsed with { RawQuestion = rawQuestion };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse query JSON — falling back to raw question. JSON: {J}", json);
            return new PastPerformanceQuery
            {
                RawQuestion   = rawQuestion,
                SemanticQuery = rawQuestion,
                Intent        = QueryIntent.General,
                TopK          = 5
            };
        }
    }
}
