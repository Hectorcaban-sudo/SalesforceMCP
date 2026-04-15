namespace SharePointRag.Core.Configuration;

// ═══════════════════════════════════════════════════════════════════════════════
//  LIBRARY DEFINITIONS
//  Each entry describes one SharePoint site + document library.
//  Libraries are the unit of crawling, downloading, and chunking.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Configuration for a single SharePoint site + document library.
/// Multiple libraries can be defined; each gets a unique Name used as a key.
/// </summary>
public class LibraryDefinition
{
    /// <summary>
    /// Unique identifier for this library, e.g. "PastPerformance", "Proposals", "HR".
    /// Used as key in RagSystemDefinition.LibraryNames.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full SharePoint site URL.</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Document library name, e.g. "Documents".</summary>
    public string LibraryName { get; set; } = "Documents";

    /// <summary>Entra ID tenant ID. If empty, falls back to the global TenantId.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>App registration client ID for Graph access to this site.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>App registration client secret. Use managed identity in production.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>File extensions to index. Empty list = all extensions.</summary>
    public List<string> AllowedExtensions { get; set; } =
        [".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".html"];

    /// <summary>Skip files larger than this (MB).</summary>
    public int MaxFileSizeMb { get; set; } = 50;

    /// <summary>Parallel download workers for this library.</summary>
    public int CrawlParallelism { get; set; } = 4;

    /// <summary>Optional sub-folder path within the library to restrict crawling.</summary>
    public string? RootFolderPath { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  RAG SYSTEM DEFINITIONS
//  A RAG system is a named logical index that aggregates one or more libraries
//  into a single SharpCoreDB HNSW collection.
//  Agents declare which RAG systems they query.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A named, logical RAG index that reads from one or more LibraryDefinitions.
///
/// Example config:
///   - Name: "PastPerformance"
///     LibraryNames: ["PastPerformanceDocs", "ProposalArchive"]
///   - Name: "HR"
///     LibraryNames: ["HRPolicies"]
///
/// Each system gets its own isolated SharpCoreDB HNSW index (sub-folder under DataDirectory).
/// A library can appear in multiple systems — it will be indexed independently into each.
/// </summary>
public class RagSystemDefinition
{
    /// <summary>
    /// Unique identifier for this RAG system, e.g. "PastPerformance", "HR", "General".
    /// Used as key when agents request their assigned stores.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Library names (keys into LibraryRegistry.Libraries) that feed this system.
    /// All matched libraries are crawled, chunked, embedded, and stored in this system's
    /// isolated HNSW index.
    /// </summary>
    public List<string> LibraryNames { get; set; } = [];

    /// <summary>Number of KNN neighbours to retrieve per query for this system.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Minimum cosine similarity threshold (0–1).</summary>
    public double MinScore { get; set; } = 0.5;

    /// <summary>Human-readable description shown in API status responses.</summary>
    public string Description { get; set; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  TOP-LEVEL REGISTRY OPTIONS
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Root configuration object binding the full multi-library, multi-system setup.
/// Bind from "RagRegistry" in appsettings.json.
/// </summary>
public class RagRegistryOptions
{
    public const string SectionName = "RagRegistry";

    /// <summary>All defined SharePoint libraries.</summary>
    public List<LibraryDefinition> Libraries { get; set; } = [];

    /// <summary>All defined RAG systems, each aggregating one or more libraries.</summary>
    public List<RagSystemDefinition> Systems { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════════
//  SHARED / GLOBAL OPTIONS (unchanged from previous version)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>Global Entra ID / Graph credentials — used as fallback when
/// a LibraryDefinition does not specify its own ClientId/Secret.</summary>
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

public class SharpCoreDbOptions
{
    public const string SectionName = "SharpCoreDB";

    /// <summary>Root directory for all SharpCoreDB files.
    /// Each RAG system gets a sub-folder: {DataDirectory}/{systemName}/</summary>
    public string DataDirectory { get; set; } = "scdb";

    /// <summary>AES-256-GCM encryption password shared across all system databases.</summary>
    public string EncryptionPassword { get; set; } = "change-me-in-production!";

    /// <summary>SQL table name for chunk metadata (same name used in every system DB).</summary>
    public string ChunksTable { get; set; } = "chunks";

    /// <summary>HNSW collection name (same name used in every system DB).</summary>
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
        You are an intelligent assistant that answers questions using content retrieved
        from the company SharePoint document library.

        Rules:
        - Only answer from the provided context chunks.
        - Always cite the source document (title + URL) for every claim.
        - If the context does not contain enough information, say so clearly.
        - Be concise but complete.
        """;

    public int MaxTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.2;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BACKWARD-COMPAT SHIM
//  Single-library deployments can still use the old "SharePoint" section.
//  The registry builder converts it to a single Library + single System.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>Legacy single-library options. Still supported via AddSharePointRag().</summary>
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
