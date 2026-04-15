using SharePointRag.Core.Configuration;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Interfaces;

// ── Per-library interfaces ────────────────────────────────────────────────────

/// <summary>Crawls a single named SharePoint document library.</summary>
public interface ISharePointCrawler
{
    string LibraryName { get; }
    IAsyncEnumerable<SharePointFile> GetFilesAsync(CancellationToken ct = default);
    Task<Stream> DownloadFileAsync(SharePointFile file, CancellationToken ct = default);
    IAsyncEnumerable<SharePointFile> GetModifiedFilesAsync(DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>Extracts plain text from various file formats.</summary>
public interface ITextExtractor
{
    Task<string> ExtractAsync(Stream content, string mimeType, string fileName, CancellationToken ct = default);
    bool CanHandle(string mimeType, string fileName);
}

/// <summary>Splits extracted text into overlapping chunks.</summary>
public interface ITextChunker
{
    IReadOnlyList<string> Chunk(string text);
}

/// <summary>Generates vector embeddings for text.</summary>
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

// ── Per-system interfaces ─────────────────────────────────────────────────────

/// <summary>
/// Vector store for a single named RAG system.
/// Each system has its own isolated SharpCoreDB HNSW index.
/// </summary>
public interface IVectorStore
{
    string SystemName { get; }
    Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryVector, int topK, double minScore, CancellationToken ct = default);
    Task DeleteByDriveItemIdAsync(string driveItemId, CancellationToken ct = default);
    Task<bool> IndexExistsAsync(CancellationToken ct = default);
    Task CreateIndexIfNotExistsAsync(CancellationToken ct = default);
}

/// <summary>
/// Persists indexing state for all files within a single RAG system.
/// Keyed by driveItemId + libraryName to support the same file appearing in multiple systems.
/// </summary>
public interface IIndexStateStore
{
    string SystemName { get; }
    Task<IndexingRecord?> GetAsync(string driveItemId, string libraryName, CancellationToken ct = default);
    Task SaveAsync(IndexingRecord record, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLastFullIndexTimeAsync(string libraryName, CancellationToken ct = default);
    Task SetLastFullIndexTimeAsync(string libraryName, DateTimeOffset time, CancellationToken ct = default);
    Task<int> GetIndexedFileCountAsync(string libraryName, CancellationToken ct = default);
}

/// <summary>
/// Indexes all libraries assigned to a single named RAG system.
/// Obtained from ILibraryRegistry by system name.
/// </summary>
public interface IIndexingPipeline
{
    string SystemName { get; }
    Task RunFullIndexAsync(CancellationToken ct = default);
    Task RunDeltaIndexAsync(CancellationToken ct = default);
}

// ── Registry interface ────────────────────────────────────────────────────────

/// <summary>
/// Central registry of all configured RAG systems and their component services.
/// Agents and controllers use this to look up systems by name at runtime.
/// </summary>
public interface ILibraryRegistry
{
    /// <summary>All configured system names.</summary>
    IReadOnlyList<string> SystemNames { get; }

    /// <summary>All configured library names.</summary>
    IReadOnlyList<string> LibraryNames { get; }

    /// <summary>Get the definition for a named system (throws if not found).</summary>
    RagSystemDefinition GetSystem(string systemName);

    /// <summary>Get the definition for a named library (throws if not found).</summary>
    LibraryDefinition GetLibrary(string libraryName);

    /// <summary>Resolve the vector store for a named system.</summary>
    IVectorStore GetVectorStore(string systemName);

    /// <summary>Resolve the indexing pipeline for a named system.</summary>
    IIndexingPipeline GetPipeline(string systemName);

    /// <summary>Resolve the index state store for a named system.</summary>
    IIndexStateStore GetStateStore(string systemName);

    /// <summary>Resolve the crawler for a named library.</summary>
    ISharePointCrawler GetCrawler(string libraryName);

    /// <summary>Collect runtime status for every system.</summary>
    Task<List<RagSystemStatus>> GetAllStatusAsync(CancellationToken ct = default);
}

// ── Multi-system RAG orchestrator ─────────────────────────────────────────────

/// <summary>
/// Fans out a question across all RAG systems assigned to an agent,
/// merges and re-ranks results, and generates a grounded answer.
/// </summary>
public interface IRagOrchestrator
{
    /// <summary>System names this orchestrator queries.</summary>
    IReadOnlyList<string> SystemNames { get; }

    Task<RagResponse> AskAsync(string question, CancellationToken ct = default);
}
