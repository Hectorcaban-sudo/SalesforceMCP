using System.Text.Json.Serialization;

namespace SharePointRag.Core.Configuration;

// ═══════════════════════════════════════════════════════════════════════════════
//  DATA SOURCE DEFINITIONS
//  Each entry is a named source of content to be indexed.
//  The Type field selects which connector handles it.
//  Properties is an open key-value bag for connector-specific config.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Supported data source connector types.
/// Add new entries here as new connectors are implemented.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataSourceType
{
    SharePoint,     // Microsoft SharePoint Online document library
    SqlDatabase,    // Any ADO.NET-compatible relational database (SQL Server, PostgreSQL, MySQL, SQLite)
    Excel,          // Local or network .xlsx / .csv files
    Deltek,         // Deltek Vantagepoint / Costpoint REST API
    Custom          // Bring-your-own IDataSourceConnector implementation
}

/// <summary>
/// Unified descriptor for any data source the RAG system can ingest.
///
/// Common fields (Name, Type, CrawlParallelism, DeltaSupported) apply to all connectors.
/// Connector-specific configuration lives in the Properties dictionary so new
/// connector types can be added without touching this class.
///
/// Example (SharePoint):
///   { "Name": "PPQDocs", "Type": "SharePoint",
///     "Properties": { "SiteUrl": "https://...", "LibraryName": "Documents", ... } }
///
/// Example (SQL):
///   { "Name": "DeltekProjects", "Type": "SqlDatabase",
///     "Properties": { "ConnectionString": "Server=...", "Query": "SELECT ...", ... } }
/// </summary>
public class DataSourceDefinition
{
    /// <summary>Unique key referenced by RagSystemDefinition.DataSourceNames.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Connector type — selects the IDataSourceConnector implementation.</summary>
    public DataSourceType Type { get; set; } = DataSourceType.SharePoint;

    /// <summary>
    /// Connector-specific configuration.
    /// See each connector's documentation for required/optional keys.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = [];

    /// <summary>Parallel ingestion workers for this source.</summary>
    public int CrawlParallelism { get; set; } = 4;

    /// <summary>
    /// Whether this source supports delta (incremental) ingestion.
    /// Set false to always do a full re-index on delta runs.
    /// </summary>
    public bool DeltaSupported { get; set; } = true;

    /// <summary>
    /// When true, this data source is managed (indexed) by an external system.
    /// This instance will:
    ///   - NOT build a connector, pipeline, or indexer for this source.
    ///   - Open the vector store in read-only mode (searches work normally).
    ///   - Block /api/index/provision, /full, and /delta for this source.
    ///   - Report "ReadOnly (externally managed)" in status responses.
    ///
    /// Use when another deployment, service, or scheduled job owns ingestion
    /// and you only want to query the already-populated store.
    /// </summary>
    public bool ReadOnly { get; set; } = false;

    /// <summary>
    /// Declared metadata schema for this data source.
    /// Defines the fields that will be present in DocumentChunk.Metadata for records
    /// from this source. Useful when:
    ///   - The source is ReadOnly and you cannot inspect its connector.
    ///   - You want to document the schema for consumers (API, PP agent, UI).
    ///   - You want the ContractExtractor to know which structured fields are available.
    ///
    /// Key   = field name (as it appears in Metadata dictionary).
    /// Value = MetadataFieldDefinition describing the field type, description, and examples.
    ///
    /// Example:
    ///   "MetadataSchema": {
    ///     "ContractNumber":  { "Type": "string",  "Description": "FAR contract number", "Examples": ["W912DQ-21-C-0042"] },
    ///     "ContractAmount":  { "Type": "decimal", "Description": "Total obligated value in USD" },
    ///     "CPARSRatingOverall": { "Type": "string", "AllowedValues": ["Exceptional","Very Good","Satisfactory","Marginal","Unsatisfactory"] }
    ///   }
    /// </summary>
    public Dictionary<string, MetadataFieldDefinition> MetadataSchema { get; set; } = [];

    // ── Convenience helpers ───────────────────────────────────────────────────

    public string Get(string key, string defaultValue = "") =>
        Properties.TryGetValue(key, out var v) ? v : defaultValue;

    public int GetInt(string key, int defaultValue = 0) =>
        int.TryParse(Get(key), out var i) ? i : defaultValue;

    public bool GetBool(string key, bool defaultValue = false) =>
        bool.TryParse(Get(key), out var b) ? b : defaultValue;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  METADATA SCHEMA DEFINITION
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Declares a single metadata field that will be present in DocumentChunk.Metadata
/// for records from a given data source.
/// </summary>
public class MetadataFieldDefinition
{
    /// <summary>
    /// Data type of this field.
    /// One of: string, integer, decimal, boolean, date, datetime, array.
    /// Used by the ContractExtractor to route structured vs narrative handling.
    /// </summary>
    public string Type { get; set; } = "string";

    /// <summary>
    /// Human-readable description of what this field contains.
    /// Surfaced in /api/index/registry and used by the PP agent to understand field semantics.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// For string fields: the only values this field can hold.
    /// Empty list means any value is valid.
    /// Example: ["Exceptional","Very Good","Satisfactory","Marginal","Unsatisfactory"]
    /// </summary>
    public List<string> AllowedValues { get; set; } = [];

    /// <summary>
    /// Representative example values to help consumers understand the format.
    /// Example: ["W912DQ-21-C-0042", "FA8726-20-D-0001"]
    /// </summary>
    public List<string> Examples { get; set; } = [];

    /// <summary>
    /// Whether this field is guaranteed to be present on every record from this source.
    /// False = field may be absent or empty on some records.
    /// </summary>
    public bool Required { get; set; } = false;

    /// <summary>
    /// Whether this field should be surfaced in contract extraction and search results.
    /// Set false to suppress internal/technical fields from UI display.
    /// </summary>
    public bool Searchable { get; set; } = true;
}

// ── Well-known property key constants per connector type ──────────────────────

/// <summary>Property keys for DataSourceType.SharePoint.</summary>
public static class SharePointProps
{
    public const string SiteUrl          = "SiteUrl";
    public const string LibraryName      = "LibraryName";
    public const string TenantId         = "TenantId";
    public const string ClientId         = "ClientId";
    public const string ClientSecret     = "ClientSecret";
    public const string AllowedExtensions= "AllowedExtensions";   // comma-separated
    public const string MaxFileSizeMb    = "MaxFileSizeMb";
    public const string RootFolderPath   = "RootFolderPath";
}

/// <summary>Property keys for DataSourceType.SqlDatabase.</summary>
public static class SqlProps
{
    public const string ConnectionString = "ConnectionString";
    /// <summary>SQL SELECT query. Must return at minimum: Id, Content. Optional: Title, Url, Author, ModifiedAt.</summary>
    public const string Query            = "Query";
    /// <summary>Column used as stable record identifier (for delta + delete).</summary>
    public const string IdColumn         = "IdColumn";
    /// <summary>Column containing the text content to embed.</summary>
    public const string ContentColumn    = "ContentColumn";
    /// <summary>Column with a display title (optional).</summary>
    public const string TitleColumn      = "TitleColumn";
    /// <summary>Column with a URL / deep-link (optional).</summary>
    public const string UrlColumn        = "UrlColumn";
    /// <summary>Column with last-modified timestamp for delta queries (optional).</summary>
    public const string ModifiedAtColumn = "ModifiedAtColumn";
    /// <summary>SQL WHERE clause appended to Query for delta runs, e.g. "ModifiedAt > @since".</summary>
    public const string DeltaFilter      = "DeltaFilter";
    /// <summary>ADO.NET provider: SqlServer (default), Postgres, MySql, Sqlite.</summary>
    public const string Provider         = "Provider";
}

/// <summary>Property keys for DataSourceType.Excel.</summary>
public static class ExcelProps
{
    /// <summary>Comma-separated glob patterns or absolute paths, e.g. "/data/*.xlsx,/data/*.csv".</summary>
    public const string FilePaths        = "FilePaths";
    /// <summary>Sheet name (Excel only). Empty = first sheet.</summary>
    public const string SheetName        = "SheetName";
    /// <summary>Column containing the text content to embed (name or 0-based index).</summary>
    public const string ContentColumn    = "ContentColumn";
    /// <summary>Column with display title.</summary>
    public const string TitleColumn      = "TitleColumn";
    /// <summary>Column with a URL / reference.</summary>
    public const string UrlColumn        = "UrlColumn";
    /// <summary>Row number of the header row (0-based). Default 0.</summary>
    public const string HeaderRow        = "HeaderRow";
}

/// <summary>Property keys for DataSourceType.Deltek.</summary>
public static class DeltekProps
{
    /// <summary>Base URL of the Deltek Vantagepoint REST API.</summary>
    public const string BaseUrl          = "BaseUrl";
    /// <summary>API key or Bearer token.</summary>
    public const string ApiKey           = "ApiKey";
    /// <summary>Comma-separated entity types to ingest: Projects, Employees, Clients, Opportunities.</summary>
    public const string Entities         = "Entities";
    /// <summary>Filter OData expression applied to all requests, e.g. "Status eq 'Active'".</summary>
    public const string Filter           = "Filter";
    /// <summary>Maximum records per page (default 100).</summary>
    public const string PageSize         = "PageSize";
}

// ═══════════════════════════════════════════════════════════════════════════════
//  RAG SYSTEM DEFINITIONS  (unchanged shape, renamed field)
// ═══════════════════════════════════════════════════════════════════════════════

public class RagSystemDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Data source names (keys into RagRegistryOptions.DataSources) that feed this system.</summary>
    public List<string> DataSourceNames { get; set; } = [];

    /// <summary>KNN neighbours to retrieve per query.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Minimum cosine similarity threshold (0–1).</summary>
    public double MinScore { get; set; } = 0.5;

    // ── Backward compat: old field name was LibraryNames ─────────────────────
    [JsonIgnore]
    public List<string> LibraryNames
    {
        get => DataSourceNames;
        set => DataSourceNames = value;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  REGISTRY ROOT
// ═══════════════════════════════════════════════════════════════════════════════

public class RagRegistryOptions
{
    public const string SectionName = "RagRegistry";

    /// <summary>All defined data sources (SharePoint, SQL, Excel, Deltek, Custom…).</summary>
    public List<DataSourceDefinition> DataSources { get; set; } = [];

    /// <summary>All defined RAG systems, each aggregating one or more data sources.</summary>
    public List<RagSystemDefinition> Systems { get; set; } = [];

    // ── Backward compat: old field name was Libraries ─────────────────────────
    [JsonIgnore]
    public List<DataSourceDefinition> Libraries
    {
        get => DataSources;
        set => DataSources = value;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  GLOBAL / SHARED OPTIONS  (unchanged)
// ═══════════════════════════════════════════════════════════════════════════════

public class GlobalGraphOptions
{
    public const string SectionName = "GlobalGraph";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = "gpt-4o";
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-large";
    public int EmbeddingDimensions { get; set; } = 3072;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  VECTOR STORE PROVIDER SELECTION
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Selects which vector store backend the RAG system uses.
/// Configure in appsettings: "VectorStore": { "Provider": "LiteGraph" }
/// </summary>
public enum VectorStoreProvider
{
    /// <summary>
    /// LiteGraph (default) — embedded SQLite, no external server.
    /// One file holds all systems as isolated Graphs under a shared Tenant.
    /// Zero infrastructure: just a file path.
    /// </summary>
    LiteGraph,

    /// <summary>
    /// ChromaDB — external HTTP vector database (requires a running Chroma server).
    /// Scales to very large corpora; supports distributed deployments.
    /// Each RAG system maps to one Chroma collection.
    /// Start server: docker run -p 8000:8000 chromadb/chroma
    /// </summary>
    Chroma
}

/// <summary>
/// Top-level vector store configuration. Set Provider to switch backends.
/// </summary>
public class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    /// <summary>Which vector store backend to use. Default: LiteGraph.</summary>
    public VectorStoreProvider Provider { get; set; } = VectorStoreProvider.LiteGraph;
}

/// <summary>
/// LiteGraph embedded SQLite graph + vector store options.
/// Each RAG system gets its own isolated graph (Tenant → Graph → Nodes).
/// The SQLite file is stored at DatabasePath.
/// </summary>
public class LiteGraphOptions
{
    public const string SectionName = "LiteGraph";

    /// <summary>
    /// Path to the SQLite database file shared across all RAG systems.
    /// Each system is isolated by its own Graph object inside a shared Tenant.
    /// Example: "/app/data/litegraph.db"
    /// </summary>
    public string DatabasePath { get; set; } = "litegraph.db";

    /// <summary>
    /// Name of the LiteGraph Tenant that owns all RAG system graphs.
    /// One tenant per deployment is sufficient; change only if you share
    /// a database with other applications.
    /// </summary>
    public string TenantName { get; set; } = "RAGSystem";

    /// <summary>
    /// Embedding model name stamped on every VectorMetadata record.
    /// Should match AzureOpenAI:EmbeddingDeployment.
    /// </summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-large";
}

/// <summary>
/// ChromaDB external HTTP vector store options.
/// Requires a running Chroma server (chromadb/chroma Docker image or hosted Chroma Cloud).
///
/// Each RAG system maps to a dedicated Chroma collection named "{CollectionPrefix}{systemName}".
/// Collections are created automatically on first use.
///
/// Docker quickstart:
///   docker run -p 8000:8000 chromadb/chroma
///
/// Chroma Cloud:
///   Set Endpoint to your cloud URL and ApiKey to your token.
///   Leave ApiKey empty for unauthenticated local deployments.
/// </summary>
public class ChromaOptions
{
    public const string SectionName = "Chroma";

    /// <summary>
    /// Full base URL of the Chroma HTTP API, including /api/v1.
    /// Example: "http://localhost:8000/api/v1/"
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:8000/api/v1/";

    /// <summary>
    /// Optional API key / Bearer token for authenticated Chroma deployments.
    /// Empty = no authentication (local / dev instances).
    /// For Chroma Cloud, set this to your API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional tenant name for Chroma multi-tenant setups.
    /// Leave empty to use the Chroma default tenant.
    /// </summary>
    public string Tenant { get; set; } = string.Empty;

    /// <summary>
    /// Optional database name for Chroma multi-tenant setups.
    /// Leave empty to use the Chroma default database.
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// Prefix applied to every collection name: "{CollectionPrefix}{systemName}".
    /// Useful when sharing a Chroma server across multiple applications.
    /// Example: "rag_" → collections "rag_General", "rag_PastPerformance", etc.
    /// </summary>
    public string CollectionPrefix { get; set; } = "rag_";

    /// <summary>
    /// Chroma's HNSW space metric.
    /// "cosine" (default) matches text-embedding-3-large best.
    /// Alternatives: "l2", "ip" (inner product).
    /// </summary>
    public string DistanceFunction { get; set; } = "cosine";

    /// <summary>
    /// Number of results to request from Chroma per query.
    /// Chroma returns distances (lower = more similar for cosine); we convert and
    /// apply MinScore filtering in the adapter.
    /// </summary>
    public int DefaultTopK { get; set; } = 20;

    /// <summary>HTTP timeout for Chroma API calls in seconds. Default: 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

// ── Backward-compat alias (referenced by older configs) ──────────────────────
[Obsolete("Use LiteGraphOptions. SharpCoreDbOptions is kept for config migration only.")]
public class SharpCoreDbOptions
{
    public const string SectionName = "SharpCoreDB";
    public string DataDirectory { get; set; } = "scdb";
    public string EncryptionPassword { get; set; } = "change-me-in-production!";
    public string ChunksTable { get; set; } = "chunks";
    public string EmbeddingsCollection { get; set; } = "chunk_embeddings";
}

public class ChunkingOptions
{
    public const string SectionName = "Chunking";
    public int MaxTokensPerChunk { get; set; } = 512;
    public int OverlapTokens { get; set; } = 64;
}


public class AgentOptions
{
    public const string SectionName = "Agent";
    public string SystemPrompt { get; set; } =
        """
        You are an intelligent assistant that answers questions using content from
        the organisation's knowledge base.

        Rules:
        - Only answer from the provided context chunks.
        - Always cite the source (title + URL or data source name) for every claim.
        - If the context does not contain enough information, say so clearly.
        - Be concise but complete.
        """;
    public int MaxTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.2;
}

// Legacy shim
public class SharePointOptions
{
    public const string SectionName = "SharePoint";
    public string SiteUrl { get; set; } = string.Empty;
    public string LibraryName { get; set; } = "Documents";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public List<string> AllowedExtensions { get; set; } =
        [".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".html"];
    public int MaxFileSizeMb { get; set; } = 50;
    public int CrawlParallelism { get; set; } = 4;
}
