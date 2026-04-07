namespace SharePointRag.Core.Models;

/// <summary>Represents a file discovered in SharePoint during crawl.</summary>
public record SharePointFile(
    string DriveItemId,
    string Name,
    string WebUrl,
    string MimeType,
    long SizeBytes,
    DateTimeOffset LastModified,
    string LibraryPath,
    string? Author
);

/// <summary>A text chunk derived from a SharePoint file, ready to embed.</summary>
public record DocumentChunk
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string DriveItemId { get; init; }
    public required string FileName { get; init; }
    public required string WebUrl { get; init; }
    public required string LibraryPath { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset LastModified { get; init; }
    public required string Content { get; init; }
    public int ChunkIndex { get; init; }
    public int TotalChunks { get; init; }
    public float[]? Embedding { get; set; }
}

/// <summary>A chunk returned from vector search with its relevance score.</summary>
public record RetrievedChunk(DocumentChunk Chunk, double Score);

/// <summary>A user question and the agent's grounded answer.</summary>
public record RagResponse(
    string Question,
    string Answer,
    IReadOnlyList<RetrievedChunk> Sources
);

/// <summary>Tracks the indexing state of a single file.</summary>
public record IndexingRecord
{
    public required string DriveItemId { get; init; }
    public required string FileName { get; init; }
    public DateTimeOffset LastIndexed { get; set; }
    public int ChunkCount { get; set; }
    public IndexingStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum IndexingStatus { Pending, Indexed, Failed, Skipped }
