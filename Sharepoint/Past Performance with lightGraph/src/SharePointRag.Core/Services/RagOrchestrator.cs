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
/// Multi-system, source-agnostic RAG orchestrator.
/// Fans out over all assigned systems in parallel, merges + re-ranks results,
/// and generates a grounded LLM answer.
/// Works identically whether chunks came from SharePoint, SQL, Excel, Deltek, or Custom.
/// </summary>
public sealed class RagOrchestrator : IRagOrchestrator
{
    private readonly IReadOnlyList<string>    _systemNames;
    private readonly ILibraryRegistry         _registry;
    private readonly IEmbeddingService        _embedder;
    private readonly AzureOpenAIClient        _openAi;
    private readonly AzureOpenAIOptions       _aoai;
    private readonly AgentOptions             _agent;
    private readonly ILogger<RagOrchestrator> _logger;

    public IReadOnlyList<string> SystemNames => _systemNames;

    public RagOrchestrator(
        IReadOnlyList<string> systemNames,
        ILibraryRegistry registry,
        IEmbeddingService embedder,
        AzureOpenAIClient openAi,
        IOptions<AzureOpenAIOptions> aoaiOpts,
        IOptions<AgentOptions> agentOpts,
        ILogger<RagOrchestrator> logger)
    {
        _systemNames = systemNames;
        _registry    = registry;
        _embedder    = embedder;
        _openAi      = openAi;
        _aoai        = aoaiOpts.Value;
        _agent       = agentOpts.Value;
        _logger      = logger;
    }

    public async Task<RagResponse> AskAsync(string question, CancellationToken ct = default)
    {
        _logger.LogInformation("RAG question across [{S}]: {Q}",
            string.Join(", ", _systemNames), question);

        // 1. Embed once
        var queryVector = await _embedder.EmbedAsync(question, ct);

        // 2. Fan-out search across all assigned systems in parallel
        var searchTasks = _systemNames.Select(async name =>
        {
            var sys   = _registry.GetSystem(name);
            var store = _registry.GetVectorStore(name);
            return await store.SearchAsync(queryVector, sys.TopK, sys.MinScore, ct);
        }).ToList();

        var allResults = await Task.WhenAll(searchTasks);

        // 3. Merge + re-rank by cosine similarity score
        var merged = allResults
            .SelectMany(r => r)
            .OrderByDescending(r => r.Score)
            .ToList();

        _logger.LogInformation("Retrieved {N} chunks across {S} system(s)",
            merged.Count, _systemNames.Count);

        if (merged.Count == 0)
        {
            return new RagResponse(
                question,
                "I could not find any relevant information in the knowledge base for your question.",
                merged);
        }

        // 4. Build grounded context (source-agnostic labelling)
        var context = BuildContext(merged);

        // 5. Generate grounded answer
        var answer = await GenerateAnswerAsync(question, context, ct);
        return new RagResponse(question, answer, merged);
    }

    private static string BuildContext(IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c    = chunks[i].Chunk;
            var cite = string.IsNullOrEmpty(c.Url) ? c.DataSourceName : c.Url;

            sb.AppendLine($"[{i + 1}] Source: {c.DataSourceName} | Title: {c.Title} | Ref: {cite} | Score: {chunks[i].Score:F4}");

            // Surface connector-specific metadata as additional context
            if (c.Metadata.Count > 0)
            {
                var metaSummary = string.Join(", ",
                    c.Metadata.Where(kv => kv.Value.Length < 80)
                              .Take(4)
                              .Select(kv => $"{kv.Key}={kv.Value}"));
                if (metaSummary.Length > 0)
                    sb.AppendLine($"    [{metaSummary}]");
            }

            sb.AppendLine(c.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string> GenerateAnswerAsync(
        string question, string context, CancellationToken ct)
    {
        var client = _openAi.GetChatClient(_aoai.ChatDeployment);
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

        var opts = new ChatCompletionOptions
        {
            MaxOutputTokenCount = _agent.MaxTokens,
            Temperature         = (float)_agent.Temperature
        };

        var response = await client.CompleteChatAsync(messages, opts, ct);
        return response.Value.Content[0].Text;
    }
}
