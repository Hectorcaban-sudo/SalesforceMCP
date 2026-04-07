namespace SharePointRag.Core.Configuration;

public class SharePointOptions
{
    public const string SectionName = "SharePoint";
    public string SiteUrl { get; set; } = string.Empty;
    public string LibraryName { get; set; } = "Documents";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public List<string> AllowedExtensions { get; set; } = [".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".html"];
    public int MaxFileSizeMb { get; set; } = 50;
    public int CrawlParallelism { get; set; } = 4;
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

    /// <summary>
    /// Directory path where SharpCoreDB stores its encrypted database files.
    /// Defaults to a 'scdb' sub-folder next to the executable.
    /// </summary>
    public string DataDirectory { get; set; } = "scdb";

    /// <summary>
    /// AES-256-GCM encryption password for the database files.
    /// Set via environment variable or user-secrets — never hardcode.
    /// </summary>
    public string EncryptionPassword { get; set; } = "change-me-in-production!";

    /// <summary>Name of the SQL table that stores chunk metadata.</summary>
    public string ChunksTable { get; set; } = "chunks";

    /// <summary>Name of the embeddings collection used by VectorSearch / GraphRagEngine.</summary>
    public string EmbeddingsCollection { get; set; } = "chunk_embeddings";

    /// <summary>Number of nearest-neighbour chunks to retrieve per query.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Minimum cosine similarity score (0–1) to include a result.</summary>
    public double MinScore { get; set; } = 0.5;
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
