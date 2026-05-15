using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharePointRag.PastPerformance.Plugins;

// ═══════════════════════════════════════════════════════════════════════════════
//  OPENAI-COMPATIBLE PLUGIN
//  Works with Azure OpenAI, OpenAI API, Ollama, LM Studio, or any endpoint
//  that accepts a standard /chat/completions request.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Calls any OpenAI-compatible chat completions endpoint.
///
/// Config example (Azure OpenAI):
///   Name:         "azure_gpt4_specialist"
///   EndpointType: "OpenAiCompatible"
///   Endpoint:     "https://myresource.openai.azure.com/openai/deployments/gpt-4o"
///   ApiKey:       "{AZURE_OPENAI_KEY}"
///   Model:        "gpt-4o"
///   SystemPrompt: "You are a GovCon expert specialising in DoD contract awards."
///
/// Config example (partner Ollama):
///   Name:         "local_llama_specialist"
///   EndpointType: "OpenAiCompatible"
///   Endpoint:     "http://ollama-server:11434/v1"
///   ApiKey:       ""
///   Model:        "llama3.1:70b"
/// </summary>
public sealed class OpenAiCompatiblePlugin : IExternalAiPlugin
{
    private readonly ExternalAiPluginDefinition _def;
    private readonly HttpClient                  _http;
    private readonly ILogger                     _logger;

    public string PluginName   => _def.Name;
    public string Description  => _def.Description;

    public OpenAiCompatiblePlugin(ExternalAiPluginDefinition def, ILogger logger)
    {
        _def    = def;
        _logger = logger;
        _http   = BuildHttpClient(def);
    }

    public async Task<ExternalAiResponse> QueryAsync(
        ExternalAiRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var messages = new List<object>();

            if (!string.IsNullOrEmpty(_def.SystemPrompt))
                messages.Add(new { role = "system", content = _def.SystemPrompt });

            // Build user message with focused question and any existing context
            var userContent = string.IsNullOrEmpty(request.ExistingContext)
                ? request.FocusedQuestion
                : $"""
                   Existing context from internal knowledge base:
                   {request.ExistingContext}

                   Additional question for you specifically:
                   {request.FocusedQuestion}
                   """;

            messages.Add(new { role = "user", content = userContent });

            var body = new
            {
                model       = _def.Model,
                messages,
                max_tokens  = _def.MaxTokens,
                temperature = 0.2
            };

            // Azure OpenAI uses api-version query param; regular OpenAI does not
            var url = _def.Endpoint.TrimEnd('/');
            if (url.Contains("azure.com") && !url.Contains("?api-version"))
                url += "/chat/completions?api-version=2024-10-21";
            else if (!url.EndsWith("/chat/completions"))
                url += "/chat/completions";

            var json    = JsonSerializer.Serialize(body);
            using var resp = await _http.PostAsync(url,
                new StringContent(json, Encoding.UTF8, "application/json"), ct);

            var respBody = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            var doc     = JsonDocument.Parse(respBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            sw.Stop();
            _logger.LogDebug("[Plugin:{N}] Success in {Ms}ms", _def.Name, sw.ElapsedMilliseconds);

            return new ExternalAiResponse
            {
                PluginName        = _def.Name,
                PluginDescription = _def.Description,
                Content           = content,
                IsSuccess         = true,
                Duration          = sw.Elapsed,
                HttpStatusCode    = (int)resp.StatusCode
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Plugin:{N}] Query failed", _def.Name);
            return Fail(ex.Message, sw.Elapsed);
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await QueryAsync(new ExternalAiRequest
            {
                UserQuestion    = "ping",
                FocusedQuestion = "Reply with one word: OK"
            }, ct);
            return response.IsSuccess
                ? $"Connected to {_def.Endpoint} — model: {_def.Model}"
                : $"Reachable but error: {response.ErrorMessage}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    private ExternalAiResponse Fail(string error, TimeSpan duration) => new()
    {
        PluginName        = _def.Name,
        PluginDescription = _def.Description,
        Content           = string.Empty,
        IsSuccess         = false,
        ErrorMessage      = error,
        Duration          = duration
    };

    private static HttpClient BuildHttpClient(ExternalAiPluginDefinition def)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(def.TimeoutSeconds) };

        if (!string.IsNullOrEmpty(def.ApiKey))
        {
            // Azure OpenAI uses api-key header; others use Bearer
            if (def.Endpoint.Contains("azure.com"))
                http.DefaultRequestHeaders.Add("api-key", def.ApiKey);
            else
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", def.ApiKey);
        }

        foreach (var kv in def.Headers)
            http.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);

        return http;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  CUSTOM HTTP PLUGIN
//  Generic POST endpoint with configurable request/response shapes.
//  Use for proprietary AI systems, Deltek AI, SAM.gov, CPARS.gov portals, etc.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Calls any custom HTTP endpoint that accepts a POST request.
///
/// Config example (hypothetical SAM.gov AI):
///   Name:             "sam_gov_ai"
///   EndpointType:     "CustomHttp"
///   Endpoint:         "https://api.sam.gov/ai/v1/chat"
///   ApiKey:           "{SAM_GOV_API_KEY}"
///   RequestTemplate:  '{"query": "{question}", "context": "{context}", "type": "past_performance"}'
///   ResponsePath:     "result.answer"
///   Headers:          { "X-Api-Key": "{SAM_GOV_API_KEY}" }
///
/// Config example (internal Deltek AI endpoint):
///   Name:             "deltek_ai"
///   EndpointType:     "CustomHttp"
///   Endpoint:         "https://deltek-ai.internal.contoso.com/chat"
///   RequestTemplate:  '{"message": "{question}", "system": "PastPerformance"}'
///   ResponsePath:     "response.text"
/// </summary>
public sealed class CustomHttpPlugin : IExternalAiPlugin
{
    private readonly ExternalAiPluginDefinition _def;
    private readonly HttpClient                  _http;
    private readonly ILogger                     _logger;

    public string PluginName  => _def.Name;
    public string Description => _def.Description;

    public CustomHttpPlugin(ExternalAiPluginDefinition def, ILogger logger)
    {
        _def    = def;
        _logger = logger;
        _http   = BuildHttpClient(def);
    }

    public async Task<ExternalAiResponse> QueryAsync(
        ExternalAiRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string bodyJson;

            if (!string.IsNullOrEmpty(_def.RequestTemplate))
            {
                // Replace placeholders in the user-supplied template
                bodyJson = _def.RequestTemplate
                    .Replace("{question}", EscapeJson(request.FocusedQuestion))
                    .Replace("{context}",  EscapeJson(request.ExistingContext))
                    .Replace("{intent}",   EscapeJson(request.Intent));
            }
            else
            {
                // Default minimal body
                var body = new
                {
                    question = request.FocusedQuestion,
                    context  = request.ExistingContext,
                    intent   = request.Intent
                };
                bodyJson = JsonSerializer.Serialize(body);
            }

            using var resp = await _http.PostAsync(
                _def.Endpoint,
                new StringContent(bodyJson, Encoding.UTF8, "application/json"),
                ct);

            var respBody = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            // Extract answer via ResponsePath or use raw body
            var content = ExtractFromPath(respBody, _def.ResponsePath);

            sw.Stop();
            _logger.LogDebug("[Plugin:{N}] Success in {Ms}ms", _def.Name, sw.ElapsedMilliseconds);

            return new ExternalAiResponse
            {
                PluginName        = _def.Name,
                PluginDescription = _def.Description,
                Content           = content,
                IsSuccess         = true,
                Duration          = sw.Elapsed,
                HttpStatusCode    = (int)resp.StatusCode
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Plugin:{N}] Query failed", _def.Name);
            return new ExternalAiResponse
            {
                PluginName        = _def.Name,
                PluginDescription = _def.Description,
                Content           = string.Empty,
                IsSuccess         = false,
                ErrorMessage      = ex.Message,
                Duration          = sw.Elapsed
            };
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_def.Endpoint, ct);
            return $"HTTP {(int)resp.StatusCode} from {_def.Endpoint}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Extracts a value from a JSON response using a dot-notation path.
    /// Supports array indexing: "choices[0].message.content"
    /// </summary>
    private static string ExtractFromPath(string json, string path)
    {
        if (string.IsNullOrEmpty(path)) return json;
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            foreach (var segment in path.Split('.'))
            {
                if (node is null) return json;

                // Handle array index: choices[0]
                var bracketIdx = segment.IndexOf('[');
                if (bracketIdx >= 0)
                {
                    var key = segment[..bracketIdx];
                    var idx = int.Parse(segment[(bracketIdx + 1)..].TrimEnd(']'));
                    node = node[key]?[idx];
                }
                else
                {
                    node = node[segment];
                }
            }
            return node?.GetValue<string>() ?? node?.ToString() ?? json;
        }
        catch
        {
            return json;
        }
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    private static HttpClient BuildHttpClient(ExternalAiPluginDefinition def)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(def.TimeoutSeconds) };

        if (!string.IsNullOrEmpty(def.ApiKey))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", def.ApiKey);

        foreach (var kv in def.Headers)
            http.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);

        return http;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  MICROSOFT COPILOT / AZURE AI AGENT PLUGIN
//  Sends messages to an Azure AI Agent Service thread and polls for completion.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Calls a Microsoft Copilot agent or Azure AI Agent Service endpoint.
/// Creates a thread, sends a message, polls until complete, returns the response.
///
/// Config example:
///   Name:         "copilot_contract_analyst"
///   EndpointType: "MicrosoftCopilot"
///   Endpoint:     "https://api.agentservice.microsoft.com"
///   ApiKey:       "{BEARER_TOKEN}"
///   AgentId:      "asst_abc123xyz"
///   SystemPrompt: "Focus on GovCon past performance and FAR compliance."
/// </summary>
public sealed class MicrosoftCopilotPlugin : IExternalAiPlugin
{
    private readonly ExternalAiPluginDefinition _def;
    private readonly HttpClient                  _http;
    private readonly ILogger                     _logger;

    public string PluginName  => _def.Name;
    public string Description => _def.Description;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public MicrosoftCopilotPlugin(ExternalAiPluginDefinition def, ILogger logger)
    {
        _def    = def;
        _logger = logger;
        _http   = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(def.TimeoutSeconds),
            BaseAddress = new Uri(def.Endpoint.TrimEnd('/') + "/")
        };

        if (!string.IsNullOrEmpty(def.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", def.ApiKey);

        foreach (var kv in def.Headers)
            _http.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
    }

    public async Task<ExternalAiResponse> QueryAsync(
        ExternalAiRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 1. Create a thread
            var threadResp = await _http.PostAsync(
                "v1/threads",
                new StringContent("{}", Encoding.UTF8, "application/json"),
                ct);
            threadResp.EnsureSuccessStatusCode();
            var threadDoc = JsonDocument.Parse(await threadResp.Content.ReadAsStringAsync(ct));
            var threadId  = threadDoc.RootElement.GetProperty("id").GetString()
                            ?? throw new InvalidOperationException("No thread ID returned.");

            // 2. Add the user message
            var msgBody = JsonSerializer.Serialize(new
            {
                role    = "user",
                content = string.IsNullOrEmpty(request.ExistingContext)
                    ? request.FocusedQuestion
                    : $"Context:\n{request.ExistingContext}\n\nQuestion: {request.FocusedQuestion}"
            });
            var msgResp = await _http.PostAsync(
                $"v1/threads/{threadId}/messages",
                new StringContent(msgBody, Encoding.UTF8, "application/json"),
                ct);
            msgResp.EnsureSuccessStatusCode();

            // 3. Run the agent
            var runBody = JsonSerializer.Serialize(new
            {
                assistant_id        = _def.AgentId,
                additional_instructions = _def.SystemPrompt
            });
            var runResp = await _http.PostAsync(
                $"v1/threads/{threadId}/runs",
                new StringContent(runBody, Encoding.UTF8, "application/json"),
                ct);
            runResp.EnsureSuccessStatusCode();
            var runDoc  = JsonDocument.Parse(await runResp.Content.ReadAsStringAsync(ct));
            var runId   = runDoc.RootElement.GetProperty("id").GetString()
                          ?? throw new InvalidOperationException("No run ID returned.");

            // 4. Poll until completed
            var deadline = DateTimeOffset.UtcNow.AddSeconds(_def.TimeoutSeconds - 5);
            string status = "queued";
            while (status is "queued" or "in_progress" or "cancelling")
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException($"Agent run timed out after {_def.TimeoutSeconds}s.");

                await Task.Delay(1500, ct);

                var pollResp = await _http.GetAsync($"v1/threads/{threadId}/runs/{runId}", ct);
                var pollDoc  = JsonDocument.Parse(await pollResp.Content.ReadAsStringAsync(ct));
                status = pollDoc.RootElement.GetProperty("status").GetString() ?? "failed";
            }

            if (status != "completed")
                throw new InvalidOperationException($"Agent run ended with status: {status}");

            // 5. Get the last assistant message
            var messagesResp = await _http.GetAsync(
                $"v1/threads/{threadId}/messages?order=desc&limit=1", ct);
            messagesResp.EnsureSuccessStatusCode();
            var messagesDoc = JsonDocument.Parse(await messagesResp.Content.ReadAsStringAsync(ct));
            var content     = messagesDoc.RootElement
                .GetProperty("data")[0]
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetProperty("value")
                .GetString() ?? string.Empty;

            sw.Stop();
            _logger.LogDebug("[Plugin:{N}] Copilot responded in {Ms}ms", _def.Name, sw.ElapsedMilliseconds);

            return new ExternalAiResponse
            {
                PluginName        = _def.Name,
                PluginDescription = _def.Description,
                Content           = content,
                IsSuccess         = true,
                Duration          = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[Plugin:{N}] Copilot call failed", _def.Name);
            return new ExternalAiResponse
            {
                PluginName        = _def.Name,
                PluginDescription = _def.Description,
                Content           = string.Empty,
                IsSuccess         = false,
                ErrorMessage      = ex.Message,
                Duration          = sw.Elapsed
            };
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"v1/assistants/{_def.AgentId}", ct);
            if (resp.IsSuccessStatusCode)
            {
                var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : _def.AgentId;
                return $"Connected to Copilot agent '{name}' at {_def.Endpoint}";
            }
            return $"Agent endpoint returned HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }
}
