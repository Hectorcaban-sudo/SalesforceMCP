using System.Text.Json.Serialization;

namespace SharePointRag.PastPerformance.Plugins;

// ═══════════════════════════════════════════════════════════════════════════════
//  CONFIGURATION
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Supported external AI endpoint types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalAiEndpointType
{
    /// <summary>
    /// Any OpenAI-compatible chat completions endpoint.
    /// Works with Azure OpenAI, OpenAI API, local Ollama, LM Studio, etc.
    /// Sends: POST {Endpoint}/chat/completions  with standard messages array.
    /// </summary>
    OpenAiCompatible,

    /// <summary>
    /// Generic HTTP POST endpoint that accepts a JSON body and returns a response.
    /// RequestTemplate and ResponsePath configure the shape.
    /// Use for proprietary AI systems, Deltek Vantagepoint AI, SAM.gov, CPARS AI, etc.
    /// </summary>
    CustomHttp,

    /// <summary>
    /// Microsoft Copilot / Azure AI Agent via the Agents API (REST).
    /// Sends messages to a Copilot agent thread and polls for the response.
    /// Requires: AgentId, TenantId, and a Bearer token in ApiKey.
    /// </summary>
    MicrosoftCopilot
}

/// <summary>
/// Definition of one external AI plugin/tool the Past Performance Agent can invoke.
/// Configure in appsettings under PastPerformanceAgent.ExternalPlugins[].
/// </summary>
public class ExternalAiPluginDefinition
{
    /// <summary>
    /// Unique name used as the tool name when GPT-4o decides to call this plugin.
    /// Use lowercase_snake_case, e.g. "sam_gov_ai", "deltek_vantagepoint_ai".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short description given to GPT-4o to help it decide when to invoke this plugin.
    /// Be specific about what the system knows.
    /// Example: "Queries SAM.gov for federal contract award data and vendor past performance."
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Type of the external AI endpoint.</summary>
    public ExternalAiEndpointType EndpointType { get; set; } = ExternalAiEndpointType.OpenAiCompatible;

    /// <summary>
    /// Base URL of the external AI system.
    /// OpenAiCompatible: "https://your-resource.openai.azure.com/openai/deployments/gpt-4o"
    /// CustomHttp:       "https://your-api.example.com/ai/ask"
    /// MicrosoftCopilot: "https://api.agentservice.microsoft.com"
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// API key, Bearer token, or subscription key for the external system.
    /// Use environment variables or user secrets — never commit this to source control.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model / deployment name (OpenAiCompatible only).
    /// Example: "gpt-4o", "gpt-4o-mini", "llama3".
    /// </summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>
    /// For MicrosoftCopilot: the Agent ID from the Azure AI Agent Service portal.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Optional system prompt injected into the external AI call.
    /// Use this to prime the external system for GovCon context.
    /// If empty, no system message is prepended.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// For CustomHttp: JSON path expression to extract the answer from the response body.
    /// Supports dot notation: "result.answer", "choices[0].message.content", "data.text".
    /// If empty, the entire response body is used as the answer text.
    /// </summary>
    public string ResponsePath { get; set; } = string.Empty;

    /// <summary>
    /// For CustomHttp: JSON template for the POST body.
    /// Use {question} and {context} as placeholders.
    /// Example: {"query": "{question}", "history": [], "filter": "govcon"}
    /// If empty, sends: {"question": "...", "context": "..."}
    /// </summary>
    public string RequestTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Additional HTTP headers sent with every request.
    /// Key-value pairs, e.g. { "X-Tenant": "contoso", "Accept-Language": "en" }.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Maximum tokens for the external AI response. Default: 1000.</summary>
    public int MaxTokens { get; set; } = 1000;

    /// <summary>Timeout in seconds for the external AI call. Default: 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether this plugin is currently enabled.
    /// Set false to disable without removing the configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Intents for which this plugin is always called (bypasses GPT-4o tool-call routing).
    /// Use when you always want this plugin for specific intents regardless of question content.
    /// Example: ["GenerateVolumeSection", "IdentifyGaps"]
    /// Empty = only called when GPT-4o decides to invoke it via tool-calling.
    /// </summary>
    public List<string> AlwaysInvokeForIntents { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════════
//  RUNTIME MODELS
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Context passed to an external AI plugin describing the current question
/// and what the RAG pipeline has already retrieved.
/// </summary>
public record ExternalAiRequest
{
    /// <summary>The user's original question.</summary>
    public required string UserQuestion { get; init; }

    /// <summary>The semantic query derived from the question (for vector search).</summary>
    public string SemanticQuery { get; init; } = string.Empty;

    /// <summary>
    /// Focused sub-question the plugin should answer.
    /// Generated by GPT-4o tool-calling routing to make the plugin call precise.
    /// </summary>
    public required string FocusedQuestion { get; init; }

    /// <summary>
    /// Context already retrieved from RAG, serialised as a compact text block.
    /// The external AI can use this to avoid repeating what's already known.
    /// </summary>
    public string ExistingContext { get; init; } = string.Empty;

    /// <summary>Query intent detected by the PP orchestrator.</summary>
    public string Intent { get; init; } = string.Empty;

    /// <summary>Any filters extracted from the query (agency, NAICS, etc.)</summary>
    public Dictionary<string, string> QueryFilters { get; init; } = [];
}

/// <summary>
/// Response returned from an external AI plugin.
/// </summary>
public record ExternalAiResponse
{
    /// <summary>Name of the plugin that produced this response.</summary>
    public required string PluginName { get; init; }

    /// <summary>Human-readable description of the plugin (for citation).</summary>
    public string PluginDescription { get; init; } = string.Empty;

    /// <summary>
    /// The answer or information returned by the external AI.
    /// This is merged into the final orchestrator context.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>Error message if the call failed (IsSuccess = false).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>How long the external call took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Raw HTTP status code (if applicable).</summary>
    public int? HttpStatusCode { get; init; }
}

/// <summary>
/// Result of the plugin routing step: which plugins were invoked and what they returned.
/// </summary>
public record PluginRoutingResult
{
    /// <summary>Plugin responses, in invocation order.</summary>
    public IReadOnlyList<ExternalAiResponse> Responses { get; init; } = [];

    /// <summary>Whether any plugins were invoked.</summary>
    public bool AnyInvoked => Responses.Count > 0;

    /// <summary>Successful plugin responses only.</summary>
    public IEnumerable<ExternalAiResponse> Successes =>
        Responses.Where(r => r.IsSuccess);

    /// <summary>Failed plugin invocations.</summary>
    public IEnumerable<ExternalAiResponse> Failures =>
        Responses.Where(r => !r.IsSuccess);
}
