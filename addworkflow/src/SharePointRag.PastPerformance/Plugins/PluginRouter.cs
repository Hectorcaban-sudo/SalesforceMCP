using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Plugins;

/// <summary>
/// Routes a question to external AI plugins using GPT-4o function/tool calling.
///
/// How it works:
///
///   1. All enabled plugins are described as OpenAI tools (function definitions).
///      Each tool has a "focused_question" parameter so GPT-4o can re-frame the
///      query specifically for that system's domain.
///
///   2. The router sends the user's question to GPT-4o with those tool definitions.
///      GPT-4o decides which plugins (if any) to call and generates a focused
///      sub-question for each.
///
///   3. The selected plugins are invoked in parallel.
///
///   4. Plugins listed in AlwaysInvokeForIntents are called regardless of GPT-4o's
///      routing decision — useful when you always want a specific system consulted
///      for certain intents (e.g. always call SAM.gov AI for GenerateVolumeSection).
///
/// The router does NOT generate the final answer — it returns plugin responses
/// to the orchestrator which merges them into its own final answer context.
/// </summary>
public sealed class PluginRouter : IPluginRouter
{
    private readonly IReadOnlyList<IExternalAiPlugin>          _plugins;
    private readonly IReadOnlyList<ExternalAiPluginDefinition> _defs;
    private readonly AzureOpenAIClient                         _openAi;
    private readonly AzureOpenAIOptions                        _aoai;
    private readonly ILogger<PluginRouter>                     _logger;

    public bool HasPlugins => _plugins.Count > 0;

    public PluginRouter(
        IEnumerable<IExternalAiPlugin>          plugins,
        IEnumerable<ExternalAiPluginDefinition> defs,
        AzureOpenAIClient                       openAi,
        AzureOpenAIOptions                      aoai,
        ILogger<PluginRouter>                   logger)
    {
        _plugins = [.. plugins];
        _defs    = [.. defs];
        _openAi  = openAi;
        _aoai    = aoai;
        _logger  = logger;
    }

    public IReadOnlyList<PluginInfo> GetPluginInfo() =>
        _defs.Select(d => new PluginInfo(
            d.Name,
            d.Description,
            d.EndpointType.ToString(),
            d.Enabled)).ToList();

    public async Task<PluginRoutingResult> RouteAsync(
        ExternalAiRequest request,
        CancellationToken ct = default)
    {
        if (_plugins.Count == 0)
            return new PluginRoutingResult();

        _logger.LogDebug("[PluginRouter] Routing '{Q}' across {N} plugin(s)",
            request.FocusedQuestion, _plugins.Count);

        // Determine which plugins are "always on" for this intent
        var alwaysOn = _defs
            .Where(d => d.Enabled &&
                        d.AlwaysInvokeForIntents.Contains(request.Intent,
                            StringComparer.OrdinalIgnoreCase))
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Use GPT-4o tool-calling to pick additional plugins
        var routedNames = await RouteViaToolCallingAsync(request, alwaysOn, ct);

        // Union: always-on + GPT-4o selected (deduplicated)
        var toInvoke = alwaysOn
            .Union(routedNames, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (toInvoke.Count == 0)
        {
            _logger.LogDebug("[PluginRouter] No plugins selected.");
            return new PluginRoutingResult();
        }

        _logger.LogInformation("[PluginRouter] Invoking plugins: [{P}]",
            string.Join(", ", toInvoke));

        // Invoke selected plugins in parallel
        var tasks = _plugins
            .Where(p => toInvoke.Contains(p.PluginName))
            .Select(p => InvokePluginAsync(p, request, ct))
            .ToList();

        var responses = await Task.WhenAll(tasks);
        return new PluginRoutingResult { Responses = [.. responses] };
    }

    // ── GPT-4o tool-calling routing ───────────────────────────────────────────

    private async Task<IReadOnlyList<string>> RouteViaToolCallingAsync(
        ExternalAiRequest   request,
        HashSet<string>     alreadySelected,
        CancellationToken   ct)
    {
        // Only include plugins not already forced by AlwaysInvokeForIntents
        var candidates = _plugins
            .Where(p => !alreadySelected.Contains(p.PluginName))
            .ToList();

        if (candidates.Count == 0)
            return [];

        // Build a tool definition per candidate plugin
        var tools = candidates.Select(p =>
        {
            var def = _defs.First(d => d.Name == p.PluginName);
            return ChatTool.CreateFunctionTool(
                functionName:        p.PluginName,
                functionDescription: $"{def.Description} Use this tool to query that system.",
                functionParameters: BinaryData.FromObjectAsJson(new
                {
                    type       = "object",
                    properties = new
                    {
                        focused_question = new
                        {
                            type        = "string",
                            description = $"The specific question to send to '{def.Name}', " +
                                          "refined for that system's domain and capabilities."
                        }
                    },
                    required = new[] { "focused_question" }
                }));
        }).ToList();

        var contextSummary = request.ExistingContext.Length > 800
            ? request.ExistingContext[..800] + "…"
            : request.ExistingContext;

        var systemMsg = new SystemChatMessage(
            """
            You are a routing agent for a GovCon past performance AI system.
            You have access to external AI tools/plugins. Decide which (if any) to call
            based on the user's question and existing context.

            Rules:
            - Only invoke a tool if it can genuinely add information not already in the context.
            - You may invoke multiple tools in parallel if the question warrants it.
            - Do not invoke a tool just to repeat information already in the context.
            - If no tool would add value, output no tool calls.
            """);

        var userMsg = new UserChatMessage(
            $"""
            User question: {request.UserQuestion}
            Intent: {request.Intent}
            Existing context summary: {contextSummary}

            Which external AI tools should be called to enrich the answer?
            For each tool you select, provide a focused_question tailored for that system.
            """);

        try
        {
            var chatClient = _openAi.GetChatClient(_aoai.ChatDeployment);
            var response   = await chatClient.CompleteChatAsync(
                [systemMsg, userMsg],
                new ChatCompletionOptions
                {
                    Tools      = tools,
                    MaxOutputTokenCount = 512,
                    Temperature = 0.0f
                },
                ct);

            // Collect the plugin names GPT-4o wants to call,
            // and update the request's focused question per plugin
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var toolCall in response.Value.ToolCalls)
            {
                try
                {
                    var args     = JsonDocument.Parse(toolCall.FunctionArguments);
                    var question = args.RootElement
                        .GetProperty("focused_question").GetString()
                        ?? request.FocusedQuestion;
                    selected[toolCall.FunctionName] = question;
                }
                catch
                {
                    selected[toolCall.FunctionName] = request.FocusedQuestion;
                }
            }

            // Store the focused questions so InvokePluginAsync can use them
            _pendingFocusedQuestions = selected;
            return [.. selected.Keys];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PluginRouter] Tool-call routing failed — skipping optional plugins");
            return [];
        }
    }

    // Per-routing-call store for GPT-4o-generated focused questions
    // (thread-safe: one PluginRouter instance is reused, but routing is awaited before invoking)
    private Dictionary<string, string> _pendingFocusedQuestions = new();

    private async Task<ExternalAiResponse> InvokePluginAsync(
        IExternalAiPlugin  plugin,
        ExternalAiRequest  baseRequest,
        CancellationToken  ct)
    {
        // Use GPT-4o focused question if available, else fall back to the user's question
        var focused = _pendingFocusedQuestions.TryGetValue(plugin.PluginName, out var q)
            ? q
            : baseRequest.FocusedQuestion;

        var pluginRequest = baseRequest with { FocusedQuestion = focused };

        _logger.LogDebug("[PluginRouter] Calling '{P}': {Q}", plugin.PluginName, focused);
        return await plugin.QueryAsync(pluginRequest, ct);
    }
}
