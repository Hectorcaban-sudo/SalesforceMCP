using SharePointRag.Core.Models;

namespace SharePointRag.Core.Interfaces;

/// <summary>Crawls a SharePoint document library and yields files.</summary>
public interface ISharePointCrawler
{
    /// <summary>Enumerate all files in the configured library.</summary>
    IAsyncEnumerable<SharePointFile> GetFilesAsync(CancellationToken ct = default);

    /// <summary>Download the raw bytes of a file.</summary>
    Task<Stream> DownloadFileAsync(SharePointFile file, CancellationToken ct = default);

    /// <summary>Get files modified after a given date (for delta indexing).</summary>
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

/// <summary>Stores and retrieves document chunks in the vector index.</summary>
public interface IVectorStore
{
    Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryVector, int topK, CancellationToken ct = default);
    Task DeleteByDriveItemIdAsync(string driveItemId, CancellationToken ct = default);
    Task<bool> IndexExistsAsync(CancellationToken ct = default);
    Task CreateIndexIfNotExistsAsync(CancellationToken ct = default);
}

/// <summary>Persists which files have been indexed (supports delta runs).</summary>
public interface IIndexStateStore
{
    Task<IndexingRecord?> GetAsync(string driveItemId, CancellationToken ct = default);
    Task SaveAsync(IndexingRecord record, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLastFullIndexTimeAsync(CancellationToken ct = default);
    Task SetLastFullIndexTimeAsync(DateTimeOffset time, CancellationToken ct = default);
}

/// <summary>Orchestrates the full RAG pipeline (retrieve → augment → generate).</summary>
public interface IRagOrchestrator
{
    Task<RagResponse> AskAsync(string question, CancellationToken ct = default);
}

/// <summary>Runs a full or delta indexing job.</summary>
public interface IIndexingPipeline
{
    Task RunFullIndexAsync(CancellationToken ct = default);
    Task RunDeltaIndexAsync(CancellationToken ct = default);
}
