using Microsoft.Extensions.Logging;
using SharpCoreDB;
using SharpCoreDB.VectorSearch;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// SharpCoreDB HNSW vector store — source-agnostic.
/// Stores DocumentChunk from any connector type (SharePoint, SQL, Excel, Deltek, Custom).
/// Each RAG system gets its own encrypted sub-directory.
/// </summary>
public sealed class SharpCoreDbVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly RagSystemDefinition _system;
    private readonly SharpCoreDbOptions  _opts;
    private readonly int                 _dims;
    private readonly ILogger<SharpCoreDbVectorStore> _logger;

    private IDatabase?      _db;
    private GraphRagEngine? _engine;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public string SystemName => _system.Name;
    private string DataDir => Path.Combine(_opts.DataDirectory, _system.Name);

    public SharpCoreDbVectorStore(
        RagSystemDefinition system,
        SharpCoreDbOptions opts,
        AzureOpenAIOptions embOpts,
        ILogger<SharpCoreDbVectorStore> logger)
    {
        _system = system;
        _opts   = opts;
        _dims   = embOpts.EmbeddingDimensions;
        _logger = logger;
    }

    private async Task EnsureInitAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;
            Directory.CreateDirectory(DataDir);

            _db = new DatabaseFactory().Create(DataDir, _opts.EncryptionPassword);

            await _db.ExecuteSQLAsync(
                $"""
                CREATE TABLE IF NOT EXISTS {_opts.ChunksTable} (
                    id             TEXT PRIMARY KEY,
                    sourceId       TEXT NOT NULL,
                    dataSourceName TEXT NOT NULL,
                    title          TEXT NOT NULL,
                    url            TEXT NOT NULL,
                    author         TEXT,
                    lastModified   INTEGER NOT NULL,
                    chunkIndex     INTEGER NOT NULL,
                    totalChunks    INTEGER NOT NULL,
                    content        TEXT NOT NULL,
                    metadata       TEXT NOT NULL DEFAULT '{{}}'
                )
                """);

            await _db.ExecuteSQLAsync(
                $"CREATE INDEX IF NOT EXISTS idx_src    ON {_opts.ChunksTable}(sourceId)");
            await _db.ExecuteSQLAsync(
                $"CREATE INDEX IF NOT EXISTS idx_ds     ON {_opts.ChunksTable}(dataSourceName)");

            _engine = new GraphRagEngine(_db, _opts.ChunksTable, _opts.EmbeddingsCollection, _dims);
            await _engine.InitializeAsync();

            _initialised = true;
            _logger.LogInformation("[{Sys}] SharpCoreDB ready at '{Dir}'", _system.Name, DataDir);
        }
        finally { _initLock.Release(); }
    }

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        return true;
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
    }

    public async Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        var list = chunks.ToList();
        if (list.Count == 0) return;

        var embeddings = new List<NodeEmbedding>(list.Count);
        foreach (var c in list)
        {
            if (c.Embedding is null)
                throw new InvalidOperationException($"Chunk {c.Id} has no embedding.");

            var metaJson = System.Text.Json.JsonSerializer.Serialize(c.Metadata);

            await _db!.ExecuteSQLAsync(
                $"""
                INSERT OR REPLACE INTO {_opts.ChunksTable}
                    (id, sourceId, dataSourceName, title, url, author,
                     lastModified, chunkIndex, totalChunks, content, metadata)
                VALUES
                    (@id, @sid, @ds, @t, @url, @auth,
                     @lm, @ci, @tc, @co, @meta)
                """,
                new Dictionary<string, object?>
                {
                    ["@id"]   = c.Id,           ["@sid"]  = c.SourceId,
                    ["@ds"]   = c.DataSourceName,["@t"]    = c.Title,
                    ["@url"]  = c.Url,           ["@auth"] = (object?)c.Author ?? DBNull.Value,
                    ["@lm"]   = c.LastModified.ToUnixTimeSeconds(),
                    ["@ci"]   = c.ChunkIndex,    ["@tc"]   = c.TotalChunks,
                    ["@co"]   = c.Content,       ["@meta"] = metaJson
                });

            embeddings.Add(new NodeEmbedding(c.Id, c.Embedding));
        }

        await _engine!.IndexEmbeddingsAsync(embeddings);
    }

    public async Task DeleteBySourceIdAsync(
        string sourceId, string dataSourceName, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        await _db!.ExecuteSQLAsync(
            $"DELETE FROM {_opts.ChunksTable} WHERE sourceId=@sid AND dataSourceName=@ds",
            new Dictionary<string, object?> { ["@sid"] = sourceId, ["@ds"] = dataSourceName });
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, double minScore, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        var results   = await _engine!.SearchAsync(queryVector, topK);
        var retrieved = new List<RetrievedChunk>();

        foreach (var sr in results)
        {
            double score = TryExtractScore(sr);
            if (score < minScore) continue;

            var rows = await _db!.ExecuteQueryAsync(
                $"SELECT * FROM {_opts.ChunksTable} WHERE id=@id LIMIT 1",
                new Dictionary<string, object?> { ["@id"] = sr.NodeId });

            var row = rows.FirstOrDefault();
            if (row is null) continue;
            retrieved.Add(new RetrievedChunk(RowToChunk(row), Math.Round(score, 4)));
        }

        return retrieved;
    }

    private static double TryExtractScore(dynamic sr)
    {
        try
        {
            var p = ((object)sr).GetType().GetProperty("Score")
                    ?? ((object)sr).GetType().GetProperty("Similarity");
            if (p?.GetValue(sr) is double d) return d;
            if (p?.GetValue(sr) is float  f) return f;
        }
        catch { /* ignore */ }
        return 1.0;
    }

    private static DocumentChunk RowToChunk(IDictionary<string, object?> row)
    {
        long ts = row.TryGetValue("lastModified", out var lm) && lm is long l ? l : 0;

        Dictionary<string, string> meta = [];
        if (row.TryGetValue("metadata", out var metaObj) && metaObj is string metaJson)
            try { meta = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(metaJson) ?? []; }
            catch { /* ignore malformed */ }

        return new DocumentChunk
        {
            Id             = row["id"]?.ToString()             ?? string.Empty,
            SourceId       = row["sourceId"]?.ToString()       ?? string.Empty,
            DataSourceName = row["dataSourceName"]?.ToString() ?? string.Empty,
            Title          = row["title"]?.ToString()          ?? string.Empty,
            Url            = row["url"]?.ToString()            ?? string.Empty,
            Author         = row["author"]?.ToString(),
            LastModified   = DateTimeOffset.FromUnixTimeSeconds(ts),
            Content        = row["content"]?.ToString()        ?? string.Empty,
            ChunkIndex     = row.TryGetValue("chunkIndex",  out var ci) && ci is long ci2 ? (int)ci2 : 0,
            TotalChunks    = row.TryGetValue("totalChunks", out var tc) && tc is long tc2 ? (int)tc2 : 1,
            Metadata       = meta
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is IAsyncDisposable a) await a.DisposeAsync();
        else (_db as IDisposable)?.Dispose();
    }
}
