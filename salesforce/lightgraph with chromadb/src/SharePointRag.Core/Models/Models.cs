namespace SharePointRag.Core.Models;

/// <summary>A text chunk produced from any data source, ready to embed and store.</summary>
public record DocumentChunk
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Stable record ID within the originating data source (for delta + delete).</summary>
    public required string SourceId { get; init; }

    /// <summary>Which named data source produced this chunk.</summary>
    public required string DataSourceName { get; init; }

    /// <summary>Display title (file name, SQL row title, Deltek project name, etc.)</summary>
    public required string Title { get; init; }

    /// <summary>Deep-link URL to the original record.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Author or record owner.</summary>
    public string? Author { get; init; }

    /// <summary>When this record was last modified.</summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>Text content of this chunk.</summary>
    public required string Content { get; init; }

    public int ChunkIndex { get; init; }
    public int TotalChunks { get; init; }

    /// <summary>Arbitrary metadata from the source connector (e.g. SQL column values).</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

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

/// <summary>Indexing state for a single source record within a specific RAG system.</summary>
public record IndexingRecord
{
    /// <summary>Stable source record identifier.</summary>
    public required string SourceId { get; init; }

    /// <summary>Which data source this record belongs to.</summary>
    public required string DataSourceName { get; init; }

    /// <summary>Which RAG system this record is indexed into.</summary>
    public required string RagSystemName { get; init; }

    public string Title { get; init; } = string.Empty;
    public DateTimeOffset LastIndexed { get; set; }
    public int ChunkCount { get; set; }
    public IndexingStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum IndexingStatus { Pending, Indexed, Failed, Skipped }

// ── Status models for REST API ────────────────────────────────────────────────

public record DataSourceStatus(
    string DataSourceName,
    string ConnectorType,
    string ConnectionInfo,
    bool IsReachable,
    int IndexedRecordCount,
    DateTimeOffset? LastFullIndex,
    DateTimeOffset? LastDeltaIndex,
    string? ConnectionError
);

public record RagSystemStatus(
    string SystemName,
    string Description,
    bool IsHealthy,
    List<DataSourceStatus> DataSources
);
