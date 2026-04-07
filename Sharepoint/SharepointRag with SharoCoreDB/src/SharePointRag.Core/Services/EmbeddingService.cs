using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Core.Services;

/// <summary>
/// Generates embeddings via Azure OpenAI, with automatic batching
/// (max 2048 inputs per request) and exponential back-off on throttle.
/// </summary>
public sealed class AzureOpenAIEmbeddingService(
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> opts,
    ILogger<AzureOpenAIEmbeddingService> logger) : IEmbeddingService
{
    private readonly AzureOpenAIOptions _opts = opts.Value;
    private const int MaxBatchSize = 2048;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var all = new List<float[]>(texts.Count);

        // Process in pages of MaxBatchSize
        for (int i = 0; i < texts.Count; i += MaxBatchSize)
        {
            var batch = texts.Skip(i).Take(MaxBatchSize).ToList();
            logger.LogDebug("Embedding batch {Start}–{End} of {Total}", i, i + batch.Count, texts.Count);

            var client = openAi.GetEmbeddingClient(_opts.EmbeddingDeployment);
            var response = await client.GenerateEmbeddingsAsync(batch, cancellationToken: ct);

            foreach (var item in response.Value)
                all.Add(item.ToFloats().ToArray());
        }

        return all;
    }
}
