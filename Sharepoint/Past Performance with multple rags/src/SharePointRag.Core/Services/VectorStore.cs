using Microsoft.Extensions.Logging;
using SharpCoreDB;
using SharpCoreDB.VectorSearch;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// One SharpCoreDB HNSW vector store per named RAG system.
/// Each system gets its own encrypted sub-directory: {DataDirectory}/{systemName}/
/// so systems are fully isolated and can be backed up or deleted independently.
/// </summary>
public sealed class SharpCoreDbVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly RagSystemDefinition _system;
    private readonly SharpCoreDbOptions  _scdbOpts;
    private readonly int _dims;
    private readonly ILogger<SharpCoreDbVectorStore> _logger;

    private IDatabase?     _db;
    private GraphRagEngine? _engine;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public string SystemName => _system.Name;

    public SharpCoreDbVectorStore(
        RagSystemDefinition system,
        SharpCoreDbOptions scdbOpts,
        AzureOpenAIOptions embOpts,
        ILogger<SharpCoreDbVectorStore> logger)
    {
        _system   = system;
        _scdbOpts = scdbOpts;
        _dims     = embOpts.EmbeddingDimensions;
        _logger   = logger;
    }

    private string DataDir =>
        Path.Combine(_scdbOpts.DataDirectory, _system.Name);

    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;
            Directory.CreateDirectory(DataDir);

            var factory = new DatabaseFactory();
            _db = factory.Create(DataDir, _scdbOpts.EncryptionPassword);

            // Chunk metadata table — also tracks libraryName for provenance
            await _db.ExecuteSQLAsync(
                $"""
                CREATE TABLE IF NOT EXISTS {_scdbOpts.ChunksTable} (
                    id            TEXT PRIMARY KEY,
                    driveItemId   TEXT NOT NULL,
                    libraryName   TEXT NOT NULL,
                    fileName      TEXT NOT NULL,
                    webUrl        TEXT NOT NULL,
                    libraryPath   TEXT NOT NULL,
                    author        TEXT,
                    lastModified  INTEGER NOT NULL,
                    chunkIndex    INTEGER NOT NULL,
                    totalChunks   INTEGER NOT NULL,
                    content       TEXT NOT NULL
                )
                """);

            await _db.ExecuteSQLAsync(
                $"CREATE INDEX IF NOT EXISTS idx_drive   ON {_scdbOpts.ChunksTable}(driveItemId)");
            await _db.ExecuteSQLAsync(
                $"CREATE INDEX IF NOT EXISTS idx_library ON {_scdbOpts.ChunksTable}(libraryName)");

            _engine = new GraphRagEngine(_db, _scdbOpts.ChunksTable, _scdbOpts.EmbeddingsCollection, _dims);
            await _engine.InitializeAsync();

            _initialised = true;
            _logger.LogInformation("[{Sys}] SharpCoreDB store ready at '{Dir}'", _system.Name, DataDir);
        }
        finally { _initLock.Release(); }
    }

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        return true;
    }

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        _logger.LogInformation("[{Sys}] Schema ready.", _system.Name);
    }

    public async Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var list = chunks.ToList();
        if (list.Count == 0) return;

        var embeddings = new List<NodeEmbedding>(list.Count);
        foreach (var c in list)
        {
            if (c.Embedding is null)
                throw new InvalidOperationException($"Chunk {c.Id} has no embedding.");

            await _db!.ExecuteSQLAsync(
                $"""
                INSERT OR REPLACE INTO {_scdbOpts.ChunksTable}
                    (id, driveItemId, libraryName, fileName, webUrl, libraryPath,
                     author, lastModified, chunkIndex, totalChunks, content)
                VALUES
                    (@id, @did, @lib, @fn, @url, @lp, @auth, @lm, @ci, @tc, @co)
                """,
                new Dictionary<string, object?>
                {
                    ["@id"]   = c.Id,           ["@did"]  = c.DriveItemId,
                    ["@lib"]  = c.LibraryName,  ["@fn"]   = c.FileName,
                    ["@url"]  = c.WebUrl,        ["@lp"]   = c.LibraryPath,
                    ["@auth"] = (object?)c.Author ?? DBNull.Value,
                    ["@lm"]   = c.LastModified.ToUnixTimeSeconds(),
                    ["@ci"]   = c.ChunkIndex,   ["@tc"]   = c.TotalChunks,
                    ["@co"]   = c.Content
                });

            embeddings.Add(new NodeEmbedding(c.Id, c.Embedding));
        }

        await _engine!.IndexEmbeddingsAsync(embeddings);
        _logger.LogDebug("[{Sys}] Upserted {N} chunks.", _system.Name, list.Count);
    }

    public async Task DeleteByDriveItemIdAsync(string driveItemId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await _db!.ExecuteSQLAsync(
            $"DELETE FROM {_scdbOpts.ChunksTable} WHERE driveItemId = @did",
            new Dictionary<string, object?> { ["@did"] = driveItemId });
        _logger.LogDebug("[{Sys}] Deleted chunks for driveItemId={Id}", _system.Name, driveItemId);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, double minScore, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);

        var results  = await _engine!.SearchAsync(queryVector, topK);
        var retrieved = new List<RetrievedChunk>();

        foreach (var sr in results)
        {
            double score = TryExtractScore(sr);
            if (score < minScore) continue;

            var rows = await _db!.ExecuteQueryAsync(
                $"SELECT * FROM {_scdbOpts.ChunksTable} WHERE id = @id LIMIT 1",
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
            var type = ((object)sr).GetType();
            var prop = type.GetProperty("Score") ?? type.GetProperty("Similarity");
            if (prop?.GetValue(sr) is double d) return d;
            if (prop?.GetValue(sr) is float  f) return f;
        }
        catch { /* ignore */ }
        return 1.0;
    }

    private static DocumentChunk RowToChunk(IDictionary<string, object?> row)
    {
        long ts = row.TryGetValue("lastModified", out var lm) && lm is long l ? l : 0;
        return new DocumentChunk
        {
            Id           = row["id"]?.ToString()          ?? string.Empty,
            DriveItemId  = row["driveItemId"]?.ToString() ?? string.Empty,
            LibraryName  = row["libraryName"]?.ToString() ?? string.Empty,
            FileName     = row["fileName"]?.ToString()    ?? string.Empty,
            WebUrl       = row["webUrl"]?.ToString()      ?? string.Empty,
            LibraryPath  = row["libraryPath"]?.ToString() ?? "/",
            Author       = row["author"]?.ToString(),
            LastModified = DateTimeOffset.FromUnixTimeSeconds(ts),
            Content      = row["content"]?.ToString()     ?? string.Empty,
            ChunkIndex   = row.TryGetValue("chunkIndex",  out var ci) && ci is long ci2 ? (int)ci2 : 0,
            TotalChunks  = row.TryGetValue("totalChunks", out var tc) && tc is long tc2 ? (int)tc2 : 1,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is IAsyncDisposable a) await a.DisposeAsync();
        else if (_db is IDisposable  s) s.Dispose();
    }
}
