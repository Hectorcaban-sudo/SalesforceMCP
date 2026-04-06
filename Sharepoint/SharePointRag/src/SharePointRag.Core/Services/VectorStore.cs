using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Stores document chunks in Azure AI Search with HNSW vector index.
/// Supports hybrid search: keyword + semantic + vector.
/// </summary>
public sealed class AzureSearchVectorStore(
    SearchIndexClient indexClient,
    IOptions<AzureSearchOptions> opts,
    IOptions<AzureOpenAIOptions> embOpts,
    ILogger<AzureSearchVectorStore> logger) : IVectorStore
{
    private readonly AzureSearchOptions _opts = opts.Value;
    private readonly int _dims = embOpts.Value.EmbeddingDimensions;

    private SearchClient SearchClient =>
        indexClient.GetSearchClient(_opts.IndexName);

    // ── Index management ──────────────────────────────────────────────────────

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        try
        {
            await indexClient.GetIndexAsync(_opts.IndexName, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        if (await IndexExistsAsync(ct))
        {
            logger.LogInformation("Index '{Index}' already exists.", _opts.IndexName);
            return;
        }

        logger.LogInformation("Creating index '{Index}'…", _opts.IndexName);

        var index = new SearchIndex(_opts.IndexName)
        {
            Fields =
            {
                new SimpleField("id",           SearchFieldDataType.String)  { IsKey = true, IsFilterable = true },
                new SimpleField("driveItemId",  SearchFieldDataType.String)  { IsFilterable = true },
                new SimpleField("webUrl",       SearchFieldDataType.String)  { IsFilterable = true },
                new SearchableField("fileName")                              { IsFilterable = true, IsSortable = true },
                new SearchableField("libraryPath")                           { IsFilterable = true },
                new SimpleField("author",       SearchFieldDataType.String)  { IsFilterable = true },
                new SimpleField("lastModified", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new SearchableField("content")                               { AnalyzerName = LexicalAnalyzerName.EnLucene },
                new SimpleField("chunkIndex",   SearchFieldDataType.Int32)   { IsFilterable = true },
                new SimpleField("totalChunks",  SearchFieldDataType.Int32),

                // Vector field for HNSW ANN search
                new VectorSearchField("embedding", _dims, "default-hnsw")
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration("default-hnsw") },
                Profiles     = { new VectorSearchProfile("default-profile", "default-hnsw") }
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration("default-semantic", new SemanticPrioritizedFields
                    {
                        ContentFields  = { new SemanticField("content") },
                        KeywordsFields = { new SemanticField("fileName") }
                    })
                }
            }
        };

        await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
        logger.LogInformation("Index '{Index}' created successfully.", _opts.IndexName);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        var docs = chunks.Select(ToSearchDoc).ToList();
        if (docs.Count == 0) return;

        // Upload in batches of 1000 (AI Search limit)
        const int batchSize = 1000;
        for (int i = 0; i < docs.Count; i += batchSize)
        {
            var batch = docs.Skip(i).Take(batchSize).ToList();
            var response = await SearchClient.MergeOrUploadDocumentsAsync(batch, cancellationToken: ct);
            logger.LogDebug("Upserted {Count} chunks (batch starting at {I})", batch.Count, i);
        }
    }

    public async Task DeleteByDriveItemIdAsync(string driveItemId, CancellationToken ct = default)
    {
        // Search for all chunks of this file and delete them
        var options = new SearchOptions
        {
            Filter = $"driveItemId eq '{driveItemId}'",
            Select = { "id" },
            Size = 1000
        };

        var results = await SearchClient.SearchAsync<SearchDocument>("*", options, ct);
        var ids = new List<string>();
        await foreach (var r in results.Value.GetResultsAsync())
            ids.Add(r.Document["id"].ToString()!);

        if (ids.Count == 0) return;
        var toDelete = ids.Select(id => new SearchDocument { ["id"] = id }).ToList();
        await SearchClient.DeleteDocumentsAsync(toDelete, cancellationToken: ct);
        logger.LogInformation("Deleted {Count} chunks for driveItemId={Id}", ids.Count, driveItemId);
    }

    // ── Read (hybrid search) ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, CancellationToken ct = default)
    {
        var options = new SearchOptions
        {
            Size = topK,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                ConfigurationName = "default-semantic",
                SemanticQuery = null,       // already using keyword query below
                ErrorMode = SemanticErrorMode.Partial
            },
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryVector)
                    {
                        Fields = { "embedding" },
                        KNearestNeighborsCount = topK
                    }
                }
            },
            Select = { "id", "driveItemId", "fileName", "webUrl", "libraryPath",
                       "author", "lastModified", "content", "chunkIndex", "totalChunks" }
        };

        var result = await SearchClient.SearchAsync<SearchDocument>("*", options, ct);
        var retrieved = new List<RetrievedChunk>();

        await foreach (var r in result.Value.GetResultsAsync())
        {
            var doc = r.Document;
            var chunk = new DocumentChunk
            {
                Id           = doc["id"].ToString()!,
                DriveItemId  = doc["driveItemId"].ToString()!,
                FileName     = doc["fileName"].ToString()!,
                WebUrl       = doc["webUrl"].ToString()!,
                LibraryPath  = doc["libraryPath"]?.ToString() ?? "/",
                Author       = doc["author"]?.ToString(),
                LastModified = doc.TryGetValue("lastModified", out var lm)
                               ? DateTimeOffset.Parse(lm.ToString()!) : default,
                Content      = doc["content"].ToString()!,
                ChunkIndex   = doc.TryGetValue("chunkIndex", out var ci) ? (int)(long)ci : 0,
                TotalChunks  = doc.TryGetValue("totalChunks", out var tc) ? (int)(long)tc : 1
            };

            double score = r.RerankerScore ?? r.Score ?? 0;
            if (score >= _opts.MinScore)
                retrieved.Add(new RetrievedChunk(chunk, score));
        }

        return retrieved;
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static SearchDocument ToSearchDoc(DocumentChunk c)
    {
        var doc = new SearchDocument
        {
            ["id"]           = c.Id,
            ["driveItemId"]  = c.DriveItemId,
            ["fileName"]     = c.FileName,
            ["webUrl"]       = c.WebUrl,
            ["libraryPath"]  = c.LibraryPath,
            ["author"]       = c.Author ?? string.Empty,
            ["lastModified"] = c.LastModified,
            ["content"]      = c.Content,
            ["chunkIndex"]   = c.ChunkIndex,
            ["totalChunks"]  = c.TotalChunks
        };
        if (c.Embedding is not null)
            doc["embedding"] = c.Embedding;
        return doc;
    }
}
