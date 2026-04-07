using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Text;

namespace SharePointRag.Core.Services;

/// <summary>
/// Retrieval-Augmented Generation pipeline:
///   1. Embed the question via Azure OpenAI
///   2. KNN search via SharpCoreDB.VectorSearch (GraphRagEngine)
///   3. Build a grounded context prompt
///   4. Call gpt-4o for the final answer
/// </summary>
public sealed class RagOrchestrator(
    IEmbeddingService embedder,
    IVectorStore vectorStore,
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions>  aoaiOpts,
    IOptions<SharpCoreDbOptions>  scdbOpts,
    IOptions<AgentOptions>        agentOpts,
    ILogger<RagOrchestrator> logger) : IRagOrchestrator
{
    private readonly AzureOpenAIOptions  _aoai  = aoaiOpts.Value;
    private readonly SharpCoreDbOptions  _scdb  = scdbOpts.Value;
    private readonly AgentOptions        _agent = agentOpts.Value;

    public async Task<RagResponse> AskAsync(string question, CancellationToken ct = default)
    {
        logger.LogInformation("RAG question: {Q}", question);

        // 1. Embed
        var queryVector = await embedder.EmbedAsync(question, ct);

        // 2. Retrieve from SharpCoreDB HNSW index
        var sources = await vectorStore.SearchAsync(queryVector, _scdb.TopK, ct);
        logger.LogInformation("Retrieved {N} chunks (topK={K})", sources.Count, _scdb.TopK);

        if (sources.Count == 0)
        {
            return new RagResponse(
                question,
                "I could not find any relevant information in the document library for your question.",
                sources);
        }

        // 3. Build grounded context
        var context = BuildContext(sources);

        // 4. Generate answer
        var answer = await GenerateAnswerAsync(question, context, ct);
        return new RagResponse(question, answer, sources);
    }

    private static string BuildContext(IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i].Chunk;
            sb.AppendLine($"[{i + 1}] Source: {c.FileName}  URL: {c.WebUrl}  Score: {chunks[i].Score:F4}");
            sb.AppendLine(c.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string> GenerateAnswerAsync(string question, string context, CancellationToken ct)
    {
        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(_agent.SystemPrompt),
            new UserChatMessage(
                $"""
                ### Retrieved context
                {context}

                ### User question
                {question}
                """)
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = _agent.MaxTokens,
            Temperature         = (float)_agent.Temperature
        };

        var response = await client.CompleteChatAsync(messages, options, ct);
        return response.Value.Content[0].Text;
    }
}
