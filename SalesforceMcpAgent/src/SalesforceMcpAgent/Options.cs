namespace SalesforceMcpAgent;

/// <summary>Salesforce connection settings (from appsettings.json or environment).</summary>
public sealed class SalesforceOptions
{
    public const string Section = "Salesforce";

    /// <summary>
    /// Your Salesforce org URL, e.g. https://MyDomain.my.salesforce.com
    /// </summary>
    public string InstanceUrl { get; set; } = string.Empty;

    /// <summary>
    /// The MCP server URL copied from Salesforce Setup → MCP Servers.
    /// Example: https://MyDomain.my.salesforce.com/api/mcp/sobject-all/sse
    /// </summary>
    public string McpServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Consumer Key from the External Client App created in Salesforce Setup.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Consumer Secret (only required for web-server / client-credentials flows).
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional: a pre-obtained access token (useful for quick testing).
    /// When set, ClientId/ClientSecret are ignored.
    /// </summary>
    public string? StaticAccessToken { get; set; }
}

/// <summary>Azure OpenAI / OpenAI model settings.</summary>
public sealed class AzureOpenAIOptions
{
    public const string Section = "AzureOpenAI";

    /// <summary>Azure OpenAI endpoint, e.g. https://my-resource.openai.azure.com/</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Deployment / model name, e.g. gpt-4o or gpt-5-mini</summary>
    public string DeploymentName { get; set; } = "gpt-4o";

    /// <summary>API key (leave blank to use DefaultAzureCredential / Managed Identity).</summary>
    public string? ApiKey { get; set; }
}
