using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Models;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Prompts;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Sends retrieved document chunks to GPT-4o with a structured extraction prompt
/// and deserialises the response into strongly-typed <see cref="ContractRecord"/> objects.
///
/// Groups chunks by source file so each LLM call processes all chunks from the
/// same document together — critical for assembling complete contract records
/// that span multiple pages.
/// </summary>
public sealed class LlmContractExtractor(
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    ILogger<LlmContractExtractor> logger) : IContractExtractor
{
    private readonly AzureOpenAIOptions _aoai = aoaiOpts.Value;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task<List<ContractRecord>> ExtractAsync(
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default)
    {
        if (chunks.Count == 0) return [];

        // Group by source file so context stays coherent
        var byFile = chunks
            .GroupBy(c => c.Chunk.Title)
            .ToList();

        var allRecords = new List<ContractRecord>();

        foreach (var group in byFile)
        {
            var fileName = group.Key;
            var webUrl   = group.First().Chunk.Url;

            // Concatenate all chunks from this file (ordered by chunk index)
            var combinedText = new StringBuilder();
            foreach (var rc in group.OrderBy(c => c.Chunk.ChunkIndex))
                combinedText.AppendLine(rc.Chunk.Content).AppendLine();

            logger.LogDebug("Extracting contracts from {File} ({Chunks} chunks)", fileName, group.Count());

            var records = await ExtractFromTextAsync(
                combinedText.ToString(), fileName, webUrl, ct);

            allRecords.AddRange(records);
        }

        // Deduplicate by contract number (same number may appear in multiple files)
        return allRecords
            .GroupBy(r => r.ContractNumber.ToUpperInvariant())
            .Select(g => g.OrderByDescending(r => r.RelevanceScore).First())
            .ToList();
    }

    private async Task<List<ContractRecord>> ExtractFromTextAsync(
        string text, string sourceFile, string webUrl, CancellationToken ct)
    {
        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var userContent = PastPerformancePrompts.ContractExtractionUserTemplate
            .Replace("{sourceFile}", sourceFile)
            .Replace("{content}",    text.Length > 12_000 ? text[..12_000] : text); // stay within context

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.ContractExtractionSystem),
            new UserChatMessage(userContent)
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 2048,
            Temperature         = 0.0f
        };

        var response = await client.CompleteChatAsync(messages, options, ct);
        var json     = response.Value.Content[0].Text.Trim();

        // Strip potential markdown fences
        if (json.StartsWith("```")) json = json.Split('\n').Skip(1).SkipLast(1).Aggregate((a, b) => $"{a}\n{b}");

        try
        {
            var records = JsonSerializer.Deserialize<List<ContractRecord>>(json, _jsonOpts) ?? [];
            // Stamp source provenance
            foreach (var r in records)
            {
                var stamped = r with
                {
                    Id                  = Guid.NewGuid().ToString("N"),
                    SourceDocumentUrl   = webUrl,
                    SourceFileName      = sourceFile
                };
                records[records.IndexOf(r)] = stamped;
            }
            return records;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Contract extraction JSON parse failed for {File}. JSON: {J}",
                sourceFile, json.Length > 500 ? json[..500] : json);
            return [];
        }
    }
}
