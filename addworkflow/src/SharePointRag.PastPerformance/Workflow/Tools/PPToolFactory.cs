using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SharePointRag.Core.Interfaces;
using SharePointRag.PastPerformance.Plugins;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Workflow.Tools;

/// <summary>
/// Builds all shared <see cref="AIFunction"/> tools used across the PP workflow graph.
///
/// Three categories of tool are created here:
///
///   RAG search tool — cross-system vector search via IRagOrchestrator
///   Data source tools — one per configured data source, each does a vector search
///                       inside that source's own isolated store
///   Plugin tools — one per enabled external AI plugin (SAM.gov, Deltek AI, Copilot…)
///
/// Any agent in any sub-workflow can receive these tools on its ChatOptions.Tools.
/// The router also wraps each sub-workflow as an AIFunction tool so it can dispatch
/// by calling draft_volume(), analyse_gaps(), get_cpars_ratings() etc.
///
/// Tool names are normalised to snake_case to satisfy the OpenAI function-calling
/// naming rules (a-z, 0-9, underscores only).
/// </summary>
public static class PPToolFactory
{
    // ── RAG search tool ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates an AIFunction that calls <see cref="IRagOrchestrator.AskAsync"/>
    /// across all systems the PP agent is configured to search.
    /// The result is a compact list of matching chunks with source provenance.
    /// </summary>
    public static AIFunction CreateRagSearchTool(IRagOrchestrator ragOrchestrator) =>
        AIFunctionFactory.Create(
            async (string query, CancellationToken ct) =>
            {
                var response = await ragOrchestrator.AskAsync(query, ct);

                if (response.Sources.Count == 0)
                    return "No matching records found in the indexed knowledge base.";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {response.Sources.Count} relevant chunks:");
                foreach (var src in response.Sources.Take(8))
                {
                    sb.AppendLine($"[{src.Chunk.DataSourceName}/{src.Chunk.Title}] " +
                                  $"(score:{src.Score:F2})");
                    sb.AppendLine(src.Chunk.Content[..Math.Min(400, src.Chunk.Content.Length)]);
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            },
            new AIFunctionFactoryOptions
            {
                Name        = "search_past_performance",
                Description = "Searches all indexed past performance records across every data source " +
                              "(SharePoint, SQL, Deltek, Excel, Custom) using vector similarity. " +
                              "Use this for broad discovery when you don't know which source has the data."
            });

    // ── Per-data-source tools ─────────────────────────────────────────────────

    /// <summary>
    /// Creates one <see cref="AIFunction"/> per data source that the PP agent can access.
    /// Each tool queries the vector store for that specific source, giving the agent
    /// fine-grained control: "search only Deltek Vantagepoint" or "search only CPARS export".
    /// </summary>
    public static IReadOnlyList<AIFunction> CreateDataSourceTools(
        ILibraryRegistry      registry,
        IEmbeddingService     embedder,
        IReadOnlyList<string> ppSystemNames)
    {
        var tools = new List<AIFunction>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sysName in ppSystemNames)
        {
            RagSystemDefinition sys;
            try { sys = registry.GetSystem(sysName); }
            catch { continue; }

            foreach (var dsName in sys.DataSourceNames)
            {
                if (!seen.Add(dsName)) continue;   // already created

                DataSourceDefinition dsDef;
                IVectorStore         store;
                try
                {
                    dsDef = registry.GetDataSource(dsName);
                    store = registry.GetVectorStore(sysName, dsName);
                }
                catch { continue; }

                // Capture for lambda
                var capturedStore   = store;
                var capturedDsName  = dsName;
                var capturedType    = dsDef.Type.ToString();
                var capturedTopK    = sys.TopK;
                var capturedMinScore= sys.MinScore;

                var toolName = "search_" + Sanitise(dsName);
                var desc = $"Searches {dsName} ({capturedType}) for past performance records " +
                           $"using vector similarity. Use this when you need data specifically " +
                           $"from {dsName}.";

                tools.Add(AIFunctionFactory.Create(
                    async (string query, CancellationToken ct) =>
                    {
                        var embedding = await embedder.EmbedAsync(query, ct);
                        var results   = await capturedStore.SearchAsync(
                            embedding, capturedTopK, capturedMinScore, ct);

                        if (results.Count == 0)
                            return $"No records found in {capturedDsName} matching: {query}";

                        var sb = new StringBuilder();
                        sb.AppendLine($"Results from {capturedDsName} ({capturedType}):");
                        foreach (var r in results.Take(5))
                        {
                            sb.AppendLine($"[{r.Chunk.Title}] score:{r.Score:F2}");
                            sb.AppendLine(r.Chunk.Content[..Math.Min(500, r.Chunk.Content.Length)]);

                            // Include key metadata fields for structured sources
                            if (r.Chunk.Metadata.Count > 0)
                            {
                                var meta = string.Join(", ", r.Chunk.Metadata
                                    .Where(kv => kv.Key != "ConnectorType")
                                    .Take(6)
                                    .Select(kv => $"{kv.Key}={kv.Value}"));
                                if (!string.IsNullOrEmpty(meta))
                                    sb.AppendLine($"  Metadata: {meta}");
                            }
                            sb.AppendLine();
                        }
                        return sb.ToString().TrimEnd();
                    },
                    new AIFunctionFactoryOptions { Name = toolName, Description = desc }));
            }
        }

        return tools;
    }

    // ── External AI plugin tools ──────────────────────────────────────────────

    /// <summary>
    /// Creates one <see cref="AIFunction"/> per enabled external AI plugin.
    /// Any agent in any sub-workflow can call these to query external systems
    /// (SAM.gov, Deltek AI, Microsoft Copilot, custom REST APIs, etc.).
    /// </summary>
    public static IReadOnlyList<AIFunction> CreatePluginTools(
        IEnumerable<IExternalAiPlugin> plugins)
    {
        var tools = new List<AIFunction>();

        foreach (var plugin in plugins)
        {
            var p = plugin;   // capture
            var toolName = "call_" + Sanitise(p.PluginName);
            var desc = $"Calls the external AI system: {p.Description}. " +
                       $"Use when you need information that may not be in the internal knowledge base.";

            tools.Add(AIFunctionFactory.Create(
                async (string question, string context, CancellationToken ct) =>
                {
                    var request = new ExternalAiRequest
                    {
                        UserQuestion    = question,
                        FocusedQuestion = question,
                        ExistingContext = context,
                        Intent          = "General"
                    };
                    var response = await p.QueryAsync(request, ct);
                    return response.IsSuccess
                        ? $"[{p.PluginName}]: {response.Content}"
                        : $"[{p.PluginName}] error: {response.ErrorMessage}";
                },
                new AIFunctionFactoryOptions { Name = toolName, Description = desc }));
        }

        return tools;
    }

    // ── Sub-workflow tool wrapper ──────────────────────────────────────────────

    /// <summary>
    /// Wraps a sub-workflow <see cref="AIAgent"/> as an <see cref="AIFunction"/> so
    /// the router agent can call it via tool-calling. Each call creates a fresh
    /// <see cref="AgentSession"/> so sub-workflow state is isolated per invocation.
    /// </summary>
    public static AIFunction CreateSubWorkflowTool(
        string  toolName,
        string  description,
        AIAgent subWorkflowAgent)
    {
        var agent = subWorkflowAgent;   // capture

        return AIFunctionFactory.Create(
            async (string userRequest, CancellationToken ct) =>
            {
                var session = await agent.CreateSessionAsync(ct);
                var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.User, userRequest)
                };
                var response = await agent.RunAsync(messages, session, ct);
                return response.Messages
                    .LastOrDefault(m => m.Role == ChatRole.Assistant)
                    ?.Text ?? "No response from sub-workflow.";
            },
            new AIFunctionFactoryOptions { Name = toolName, Description = description });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalise a string to a valid OpenAI tool name (a-z, 0-9, underscores, max 64 chars).
    /// </summary>
    internal static string Sanitise(string name) =>
        new string(
            name.ToLowerInvariant()
                .Replace(" ", "_")
                .Replace("-", "_")
                .Replace(".", "_")
                .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                .ToArray()
        )[..Math.Min(64, name.Length)];
}
