namespace SharePointRag.PastPerformance.Plugins;

/// <summary>
/// Universal interface for any external AI system the Past Performance Agent
/// can call as a tool/plugin.
///
/// Built-in implementations:
///   OpenAiCompatiblePlugin   — any OpenAI chat completions endpoint
///   CustomHttpPlugin         — arbitrary HTTP POST endpoint
///   MicrosoftCopilotPlugin   — Azure AI Agent / Copilot agent thread
///
/// Custom implementations:
///   Implement this interface and register via AddExternalAiPlugin&lt;T&gt;().
/// </summary>
public interface IExternalAiPlugin
{
    /// <summary>
    /// Plugin name — must match ExternalAiPluginDefinition.Name in config.
    /// Used as the tool name in GPT-4o tool-call routing.
    /// </summary>
    string PluginName { get; }

    /// <summary>
    /// Human-readable description of what this plugin provides.
    /// Shown in citations and the /sources API response.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Send a question + context to the external AI system and return its answer.
    /// Implementations should be fault-tolerant: catch exceptions and return
    /// ExternalAiResponse with IsSuccess=false rather than throwing.
    /// </summary>
    Task<ExternalAiResponse> QueryAsync(
        ExternalAiRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Test connectivity to the external AI system.
    /// Returns a human-readable status string (analogous to IDataSourceConnector.TestConnectionAsync).
    /// </summary>
    Task<string> TestConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Routes a PP question to zero or more external AI plugins.
/// Uses GPT-4o tool-calling to decide which plugins to invoke and
/// with what focused sub-question; then merges the results into the response.
/// </summary>
public interface IPluginRouter
{
    /// <summary>
    /// Whether any plugins are registered and enabled.
    /// The orchestrator skips routing entirely when false.
    /// </summary>
    bool HasPlugins { get; }

    /// <summary>
    /// Decide which plugins to call for this question + intent, invoke them
    /// (in parallel), and return all responses.
    /// </summary>
    Task<PluginRoutingResult> RouteAsync(
        ExternalAiRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Return info about all registered plugins (for /api/pastperformance/plugins endpoint).
    /// </summary>
    IReadOnlyList<PluginInfo> GetPluginInfo();
}

/// <summary>Summary of a registered plugin for the REST API.</summary>
public record PluginInfo(
    string Name,
    string Description,
    string EndpointType,
    bool   Enabled
);
