using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Prompts;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Sends the user's raw question to GPT-4o and extracts a structured
/// <see cref="PastPerformanceQuery"/> with intent, filters, and a
/// dense semantic-search phrase ready for vector lookup.
///
/// When an <see cref="AvailableSourcesContext"/> is supplied, the system prompt
/// is augmented with the actual data source names configured at runtime so
/// GPT-4o can produce accurate DataSourceFilter and ConnectorTypeFilter values
/// rather than guessing from training data.
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

    public async Task<PastPerformanceQuery> ParseAsync(
        string rawQuestion,
        AvailableSourcesContext? sources = null,
        CancellationToken ct = default)
    {
        logger.LogDebug("Parsing query intent for: {Q}", rawQuestion);

        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        // Build system prompt — inject runtime source list when available
        var systemPrompt = sources is not null
            ? BuildSourceAwarePrompt(sources)
            : PastPerformancePrompts.QueryParserSystem;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(
                PastPerformancePrompts.QueryParserUserTemplate
                    .Replace("{question}", rawQuestion))
        };

        var response = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 512, Temperature = 0.0f }, ct);

        var json = response.Value.Content[0].Text.Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<PastPerformanceQuery>(json, _jsonOpts)
                         ?? new PastPerformanceQuery { RawQuestion = rawQuestion, SemanticQuery = rawQuestion };

            return parsed with { RawQuestion = rawQuestion };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Failed to parse query JSON — falling back to raw question. JSON: {J}", json);
            return new PastPerformanceQuery
            {
                RawQuestion   = rawQuestion,
                SemanticQuery = rawQuestion,
                Intent        = QueryIntent.General,
                TopK          = 5
            };
        }
    }

    /// <summary>
    /// Augments the base system prompt with a runtime list of available data sources
    /// so GPT-4o can emit DataSourceFilter / ConnectorTypeFilter values that match
    /// actual configuration rather than guessing from training data.
    /// </summary>
    private static string BuildSourceAwarePrompt(AvailableSourcesContext ctx)
    {
        var sb = new StringBuilder(PastPerformancePrompts.QueryParserSystem);

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("AVAILABLE DATA SOURCES IN THIS DEPLOYMENT:");
        sb.AppendLine("(Use these exact names in DataSourceFilter and ConnectorTypeFilter)");
        sb.AppendLine();

        foreach (var ds in ctx.DataSourceNames)
            sb.AppendLine($"  - \"{ds}\"");

        sb.AppendLine();
        sb.AppendLine("AVAILABLE CONNECTOR TYPES:");
        foreach (var ct in ctx.ConnectorTypes)
            sb.AppendLine($"  - \"{ct}\"");

        sb.AppendLine();
        sb.AppendLine("SEARCHABLE RAG SYSTEMS:");
        foreach (var sys in ctx.SystemNames)
            sb.AppendLine($"  - \"{sys}\"");

        sb.AppendLine();
        sb.AppendLine("Rules for source filtering:");
        sb.AppendLine("- Only set DataSourceFilter if the user explicitly names a source from the list above.");
        sb.AppendLine("- Only set ConnectorTypeFilter if the user mentions a connector type from the list above.");
        sb.AppendLine("- Use the EXACT strings from the list — case-sensitive.");

        return sb.ToString();
    }
}
