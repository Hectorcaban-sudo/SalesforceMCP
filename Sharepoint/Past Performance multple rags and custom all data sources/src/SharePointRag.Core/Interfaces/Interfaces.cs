using SharePointRag.Core.Configuration;
using SharePointRag.Core.Models;

namespace SharePointRag.Core.Interfaces;

// ═══════════════════════════════════════════════════════════════════════════════
//  CORE DATA SOURCE CONNECTOR ABSTRACTION
//  This is the single interface that ALL data sources implement.
//  SharePoint, SQL, Excel, Deltek, and custom sources all produce
//  SourceRecord streams — the pipeline never knows which type it's talking to.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Universal source record emitted by any data source connector.
/// All fields except Id and Content are optional — connectors populate
/// what their source provides.
/// </summary>
public record SourceRecord
{
    /// <summary>Stable unique identifier within this data source (used for delta + delete).</summary>
    public required string Id { get; init; }

    /// <summary>Full text content to be chunked and embedded.</summary>
    public required string Content { get; init; }

    /// <summary>Display title (file name, row title, record name, etc.)</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Deep-link URL to the original record (SharePoint URL, CRM link, etc.)</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Author or owner of this record.</summary>
    public string? Author { get; init; }

    /// <summary>When this record was last modified (drives delta ingestion).</summary>
    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>MIME type of the original content (for text extractors; empty for pre-extracted text).</summary>
    public string MimeType { get; init; } = "text/plain";

    /// <summary>Original binary content (set when MimeType is not text/plain).</summary>
    public Stream? RawContent { get; init; }

    /// <summary>Arbitrary key-value metadata preserved on the DocumentChunk.</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>Which named data source produced this record.</summary>
    public string DataSourceName { get; init; } = string.Empty;
}

/// <summary>
/// Universal data source connector.
/// Implement this interface to add any new source — the indexing pipeline
/// calls only these three methods, knowing nothing about the underlying system.
/// </summary>
public interface IDataSourceConnector
{
    /// <summary>The data source Name (matches DataSourceDefinition.Name in config).</summary>
    string DataSourceName { get; }

    /// <summary>The type of this connector.</summary>
    DataSourceType ConnectorType { get; }

    /// <summary>Enumerate all records from this source.</summary>
    IAsyncEnumerable<SourceRecord> GetRecordsAsync(CancellationToken ct = default);

    /// <summary>
    /// Enumerate only records modified since the given timestamp.
    /// If DeltaSupported=false in config, the pipeline will call GetRecordsAsync instead.
    /// </summary>
    IAsyncEnumerable<SourceRecord> GetModifiedRecordsAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Test the connection and return a human-readable status message.
    /// Used by GET /api/index/status.
    /// </summary>
    Task<string> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Factory that creates the correct IDataSourceConnector for a DataSourceDefinition.
/// Register new connector types by implementing IDataSourceConnectorFactory
/// or by calling AddConnector() on the DataSourceConnectorRegistry.
/// </summary>
public interface IDataSourceConnectorFactory
{
    bool CanCreate(DataSourceType type);
    IDataSourceConnector Create(DataSourceDefinition definition);
}

/// <summary>
/// Registry of all connector factories.
/// Third-party / custom connectors register themselves here.
/// </summary>
public interface IConnectorRegistry
{
    void Register(IDataSourceConnectorFactory factory);
    IDataSourceConnector Resolve(DataSourceDefinition definition);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  TEXT PROCESSING
// ═══════════════════════════════════════════════════════════════════════════════

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

// ═══════════════════════════════════════════════════════════════════════════════
//  PER-SYSTEM INTERFACES  (unchanged shape — pipeline/vector store/state store)
// ═══════════════════════════════════════════════════════════════════════════════

public interface IVectorStore
{
    string SystemName { get; }
    Task UpsertAsync(IEnumerable<DocumentChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryVector, int topK, double minScore, CancellationToken ct = default);
    Task DeleteBySourceIdAsync(string sourceId, string dataSourceName, CancellationToken ct = default);
    Task<bool> IndexExistsAsync(CancellationToken ct = default);
    Task CreateIndexIfNotExistsAsync(CancellationToken ct = default);
}

public interface IIndexStateStore
{
    string SystemName { get; }
    Task<IndexingRecord?> GetAsync(string sourceId, string dataSourceName, CancellationToken ct = default);
    Task SaveAsync(IndexingRecord record, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLastFullIndexTimeAsync(string dataSourceName, CancellationToken ct = default);
    Task SetLastFullIndexTimeAsync(string dataSourceName, DateTimeOffset time, CancellationToken ct = default);
    Task<int> GetIndexedRecordCountAsync(string dataSourceName, CancellationToken ct = default);
}

public interface IIndexingPipeline
{
    string SystemName { get; }
    Task RunFullIndexAsync(CancellationToken ct = default);
    Task RunDeltaIndexAsync(CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  CENTRAL REGISTRY
// ═══════════════════════════════════════════════════════════════════════════════

public interface ILibraryRegistry
{
    IReadOnlyList<string> SystemNames { get; }
    IReadOnlyList<string> DataSourceNames { get; }
    RagSystemDefinition GetSystem(string systemName);
    DataSourceDefinition GetDataSource(string dataSourceName);
    IVectorStore GetVectorStore(string systemName);
    IIndexingPipeline GetPipeline(string systemName);
    IIndexStateStore GetStateStore(string systemName);
    IDataSourceConnector GetConnector(string dataSourceName);
    Task<List<RagSystemStatus>> GetAllStatusAsync(CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ORCHESTRATOR
// ═══════════════════════════════════════════════════════════════════════════════

public interface IRagOrchestrator
{
    IReadOnlyList<string> SystemNames { get; }
    Task<RagResponse> AskAsync(string question, CancellationToken ct = default);
}
