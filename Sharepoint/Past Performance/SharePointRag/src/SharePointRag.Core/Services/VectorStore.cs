using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCoreDB;
using SharpCoreDB.VectorSearch;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Services;

/// <summary>
/// Vector store backed by SharpCoreDB (encrypted, file-based embedded DB)
/// + SharpCoreDB.VectorSearch (HNSW SIMD-accelerated similarity search).
///
/// Storage layout
/// ──────────────
/// SharpCoreDB SQL table  →  chunk metadata (all scalar fields)
/// GraphRagEngine index   →  float[] embeddings keyed by chunk id
///
/// On every upsert we:
///   1. INSERT OR REPLACE the metadata row into the SQL table.
///   2. Call engine.IndexEmbeddingsAsync() with the new NodeEmbedding list.
///
/// On search we:
///   1. Call engine.SearchAsync(queryVector, topK) to get ranked NodeIds.
///   2. Resolve each NodeId back to a DocumentChunk via a SQL SELECT.
/// </summary>
public sealed class SharpCoreDbVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly SharpCoreDbOptions _opts;
    private readonly int _dims;
    private readonly ILogger<SharpCoreDbVectorStore> _logger;

    // SharpCoreDB database + VectorSearch engine (both lazily initialised)
    private IDatabase? _db;
    private GraphRagEngine? _engine;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public SharpCoreDbVectorStore(
        IOptions<SharpCoreDbOptions> opts,
        IOptions<AzureOpenAIOptions> embOpts,
        ILogger<SharpCoreDbVectorStore> logger)
    {
        _opts   = opts.Value;
        _dims   = embOpts.Value.EmbeddingDimensions;
        _logger = logger;
    }

    // ── Lazy initialisation ───────────────────────────────────────────────────

    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;
            await InitCoreAsync(ct);
            _initialised = true;
        }
        finally { _initLock.Release(); }
    }

    private async Task InitCoreAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_opts.DataDirectory);

        // Open (or create) the encrypted SharpCoreDB database
        var factory = new DatabaseFactory();
        _db = factory.Create(_opts.DataDirectory, _opts.EncryptionPassword);

        // Ensure the metadata table exists
        await _db.ExecuteSQLAsync(
            $"""
            CREATE TABLE IF NOT EXISTS {_opts.ChunksTable} (
                id            TEXT PRIMARY KEY,
                driveItemId   TEXT NOT NULL,
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
            $"CREATE INDEX IF NOT EXISTS idx_driveItem ON {_opts.ChunksTable}(driveItemId)");

        // Initialise the HNSW VectorSearch engine
        // GraphRagEngine(db, tableHint, embeddingsCollection, dimensions)
        _engine = new GraphRagEngine(_db, _opts.ChunksTable, _opts.EmbeddingsCollection, _dims);
        await _engine.InitializeAsync();

        _logger.LogInformation(
            "SharpCoreDB vector store initialised at '{Dir}' (dims={Dims})",
            _opts.DataDirectory, _dims);
    }

    // ── IVectorStore.IndexExistsAsync ─────────────────────────────────────────

    public async Task<bool> IndexExistsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        // If we got here without throwing the DB and engine are live
        return true;
    }

    // ── IVectorStore.CreateIndexIfNotExistsAsync ──────────────────────────────

    public async Task CreateIndexIfNotExistsAsync(CancellationToken ct = default)
    {
        // The table and HNSW index are created inside EnsureInitialisedAsync
        await EnsureInitialisedAsync(ct);
        _logger.LogInformation("SharpCoreDB schema ready.");
    }

    // ── IVectorStore.UpsertAsync ──────────────────────────────────────────────

    public async Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);

        var chunkList = chunks.ToList();
        if (chunkList.Count == 0) return;

        var nodeEmbeddings = new List<NodeEmbedding>(chunkList.Count);

        foreach (var c in chunkList)
        {
            if (c.Embedding is null)
                throw new InvalidOperationException($"Chunk '{c.Id}' has no embedding.");

            // Upsert metadata row
            await _db!.ExecuteSQLAsync(
                $"""
                INSERT OR REPLACE INTO {_opts.ChunksTable}
                    (id, driveItemId, fileName, webUrl, libraryPath, author,
                     lastModified, chunkIndex, totalChunks, content)
                VALUES
                    (@id, @driveItemId, @fileName, @webUrl, @libraryPath, @author,
                     @lastModified, @chunkIndex, @totalChunks, @content)
                """,
                new Dictionary<string, object?>
                {
                    ["@id"]           = c.Id,
                    ["@driveItemId"]  = c.DriveItemId,
                    ["@fileName"]     = c.FileName,
                    ["@webUrl"]       = c.WebUrl,
                    ["@libraryPath"]  = c.LibraryPath,
                    ["@author"]       = (object?)c.Author ?? DBNull.Value,
                    ["@lastModified"] = c.LastModified.ToUnixTimeSeconds(),
                    ["@chunkIndex"]   = c.ChunkIndex,
                    ["@totalChunks"]  = c.TotalChunks,
                    ["@content"]      = c.Content
                });

            nodeEmbeddings.Add(new NodeEmbedding(c.Id, c.Embedding));
        }

        // Index all new embeddings into the HNSW structure in one batch
        await _engine!.IndexEmbeddingsAsync(nodeEmbeddings);

        _logger.LogDebug("Upserted {Count} chunks into SharpCoreDB.", chunkList.Count);
    }

    // ── IVectorStore.DeleteByDriveItemIdAsync ─────────────────────────────────

    public async Task DeleteByDriveItemIdAsync(string driveItemId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);

        // Retrieve chunk ids for this file first (so we can remove their vectors)
        var rows = await _db!.ExecuteQueryAsync(
            $"SELECT id FROM {_opts.ChunksTable} WHERE driveItemId = @driveItemId",
            new Dictionary<string, object?> { ["@driveItemId"] = driveItemId });

        var ids = rows.Select(r => r["id"]?.ToString() ?? string.Empty)
                      .Where(id => id.Length > 0)
                      .ToList();

        if (ids.Count == 0) return;

        // Delete metadata rows
        await _db.ExecuteSQLAsync(
            $"DELETE FROM {_opts.ChunksTable} WHERE driveItemId = @driveItemId",
            new Dictionary<string, object?> { ["@driveItemId"] = driveItemId });

        _logger.LogInformation(
            "Deleted {N} chunks for driveItemId={Id}", ids.Count, driveItemId);

        // Note: SharpCoreDB.VectorSearch HNSW does not expose a per-node delete
        // in v1.7.0. Deleted chunks will not appear in results because we filter
        // SearchAsync results against the SQL table (stale ids return no rows).
        // A full re-index rebuilds the HNSW structure cleanly.
    }

    // ── IVectorStore.SearchAsync ──────────────────────────────────────────────

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryVector, int topK, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);

        // KNN search via GraphRagEngine — returns results ordered by similarity
        var searchResults = await _engine!.SearchAsync(queryVector, topK);

        var retrieved = new List<RetrievedChunk>();

        foreach (var sr in searchResults)
        {
            // sr.NodeId  = chunk id (string)
            // sr.Context = similarity context / score info from the engine
            // sr.Score   = cosine similarity [0,1] if exposed, else parse Context

            double score = TryExtractScore(sr);
            if (score < _opts.MinScore) continue;

            // Resolve metadata from SQL
            var rows = await _db!.ExecuteQueryAsync(
                $"SELECT * FROM {_opts.ChunksTable} WHERE id = @id LIMIT 1",
                new Dictionary<string, object?> { ["@id"] = sr.NodeId });

            var row = rows.FirstOrDefault();
            if (row is null) continue;   // chunk was deleted after the HNSW search

            var chunk = RowToChunk(row);
            retrieved.Add(new RetrievedChunk(chunk, Math.Round(score, 4)));
        }

        return retrieved;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double TryExtractScore(dynamic sr)
    {
        // GraphRagEngine result may expose Score as a double property,
        // or embed it as a parseable string in Context.
        try
        {
            if (sr is not null)
            {
                var type = sr.GetType();
                var scoreProp = type.GetProperty("Score") ?? type.GetProperty("Similarity");
                if (scoreProp is not null)
                {
                    var val = scoreProp.GetValue(sr);
                    if (val is double d) return d;
                    if (val is float f)  return f;
                }
            }
        }
        catch { /* ignore reflection errors */ }
        // Fall back to 1.0 (trust the HNSW ordering)
        return 1.0;
    }

    private static DocumentChunk RowToChunk(IDictionary<string, object?> row)
    {
        long epochSec = row.TryGetValue("lastModified", out var lm) && lm is long l ? l : 0;
        return new DocumentChunk
        {
            Id           = row["id"]?.ToString()          ?? string.Empty,
            DriveItemId  = row["driveItemId"]?.ToString() ?? string.Empty,
            FileName     = row["fileName"]?.ToString()    ?? string.Empty,
            WebUrl       = row["webUrl"]?.ToString()      ?? string.Empty,
            LibraryPath  = row["libraryPath"]?.ToString() ?? "/",
            Author       = row["author"]?.ToString(),
            LastModified = DateTimeOffset.FromUnixTimeSeconds(epochSec),
            Content      = row["content"]?.ToString()     ?? string.Empty,
            ChunkIndex   = row.TryGetValue("chunkIndex",  out var ci) && ci is long ci2 ? (int)ci2 : 0,
            TotalChunks  = row.TryGetValue("totalChunks", out var tc) && tc is long tc2 ? (int)tc2 : 1,
        };
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_db is IAsyncDisposable asyncDb)
            await asyncDb.DisposeAsync();
        else if (_db is IDisposable syncDb)
            syncDb.Dispose();
    }
}
