namespace SharePointRag.Core.Configuration;

public class SharePointOptions
{
    public const string SectionName = "SharePoint";

    /// <summary>SharePoint site URL, e.g. https://contoso.sharepoint.com/sites/MyLib</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>Drive / document library name, e.g. "Documents"</summary>
    public string LibraryName { get; set; } = "Documents";

    /// <summary>Entra ID tenant ID</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>App registration client ID</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>App registration client secret (use managed identity in prod)</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>File extensions to index (null = all)</summary>
    public List<string> AllowedExtensions { get; set; } = [".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".html"];

    /// <summary>Max file size in MB to index (skip larger)</summary>
    public int MaxFileSizeMb { get; set; } = 50;

    /// <summary>How many pages to crawl in parallel</summary>
    public int CrawlParallelism { get; set; } = 4;
}

public class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chat completion deployment name</summary>
    public string ChatDeployment { get; set; } = "gpt-4o";

    /// <summary>Embedding deployment name (text-embedding-3-large recommended)</summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-large";

    /// <summary>Embedding vector dimensions</summary>
    public int EmbeddingDimensions { get; set; } = 3072;
}

public class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string IndexName { get; set; } = "sharepoint-rag";

    /// <summary>Number of nearest-neighbour docs to retrieve</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Minimum semantic score threshold (0-1)</summary>
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
