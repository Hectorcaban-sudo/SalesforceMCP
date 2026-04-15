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
    string? Author,
    /// <summary>The library name (key from RagRegistryOptions) this file came from.</summary>
    string LibraryName = ""
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

    /// <summary>Which named library this chunk originated from.</summary>
    public string LibraryName { get; init; } = string.Empty;
}

/// <summary>A chunk returned from vector search with its relevance score.</summary>
public record RetrievedChunk(DocumentChunk Chunk, double Score);

/// <summary>A user question and the agent's grounded answer.</summary>
public record RagResponse(
    string Question,
    string Answer,
    IReadOnlyList<RetrievedChunk> Sources
);

/// <summary>Tracks the indexing state of a single file within a specific RAG system.</summary>
public record IndexingRecord
{
    public required string DriveItemId { get; init; }
    public required string FileName { get; init; }
    /// <summary>Which RAG system this record belongs to.</summary>
    public required string RagSystemName { get; init; }
    /// <summary>Which library this file came from.</summary>
    public required string LibraryName { get; init; }
    public DateTimeOffset LastIndexed { get; set; }
    public int ChunkCount { get; set; }
    public IndexingStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum IndexingStatus { Pending, Indexed, Failed, Skipped }

// ── Library & system status (for REST API) ────────────────────────────────────

/// <summary>Runtime status of a single library within a RAG system.</summary>
public record LibraryStatus(
    string LibraryName,
    string SiteUrl,
    string DocumentLibrary,
    bool IndexExists,
    int IndexedFileCount,
    DateTimeOffset? LastFullIndex,
    DateTimeOffset? LastDeltaIndex
);

/// <summary>Runtime status of a full RAG system (aggregates library statuses).</summary>
public record RagSystemStatus(
    string SystemName,
    string Description,
    bool IsHealthy,
    List<LibraryStatus> Libraries
);
