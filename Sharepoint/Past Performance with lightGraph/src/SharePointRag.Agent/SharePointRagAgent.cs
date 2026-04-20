using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointRag.Core.Extensions;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Text;

namespace SharePointRag.Agent;

/// <summary>
/// General-purpose SharePoint knowledge bot.
/// Declares the RAG systems it queries at construction time via the factory.
/// The system names must match keys defined in appsettings RagRegistry.Systems.
/// </summary>
public sealed class SharePointRagAgent : AgentApplication
{
    private readonly IRagOrchestrator _rag;
    private readonly ILogger<SharePointRagAgent> _logger;

    public SharePointRagAgent(
        AgentApplicationOptions options,
        IRagOrchestratorFactory factory,
        IOptions<SharePointRagAgentOptions> agentOpts,
        ILogger<SharePointRagAgent> logger)
        : base(options)
    {
        _logger = logger;

        // Declare which RAG systems this agent searches
        var systemNames = agentOpts.Value.SystemNames.Count > 0
            ? agentOpts.Value.SystemNames
            : ["General"];

        _rag = factory.Create(systemNames);

        OnActivity(ActivityTypes.Message,           OnMessageAsync);
        OnActivity(ActivityTypes.ConversationUpdate, OnConversationUpdateAsync);
        OnMessage("/help",   OnHelpAsync);
        OnMessage("/status", OnStatusAsync);
    }

    private async Task OnConversationUpdateAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        if (ctx.Activity.MembersAdded?.Any(m => m.Id != ctx.Activity.Recipient.Id) == true)
        {
            await ctx.SendActivityAsync(
                $"""
                👋 **Hello!** I'm the SharePoint Knowledge Assistant.
                I search across these document libraries: **{string.Join(", ", _rag.SystemNames)}**

                Ask me anything about your documents and I'll provide grounded answers with source links.
                Type **/help** for usage tips.
                """, ct);
        }
    }

    private async Task OnMessageAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var question = ctx.Activity.Text?.Trim();
        if (string.IsNullOrEmpty(question)) return;

        await ctx.SendActivityAsync(Activity.CreateTypingActivity(), ct);

        try
        {
            var response = await _rag.AskAsync(question, ct);
            await ctx.SendActivityAsync(FormatReply(response), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG pipeline failed for: {Q}", question);
            await ctx.SendActivityAsync("⚠️ An error occurred while searching. Please try again.", ct);
        }
    }

    private async Task OnHelpAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            $"""
            **SharePoint Knowledge Assistant – Help**

            I search these RAG systems: **{string.Join(", ", _rag.SystemNames)}**

            Simply type your question. Results are ranked by relevance across all systems.

            **Commands:** `/help` · `/status`
            """, ct);
    }

    private async Task OnStatusAsync(ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            $"📊 **Systems:** {string.Join(", ", _rag.SystemNames)} | " +
            "Vector store: LiteGraph SQLite (persistent graph + vector) | LLM: Azure OpenAI GPT-4o", ct);
    }

    private static string FormatReply(RagResponse response)
    {
        var sb = new StringBuilder();
        sb.AppendLine(response.Answer);

        if (response.Sources.Count > 0)
        {
            sb.AppendLine().AppendLine("---").AppendLine("**Sources**");
            for (int i = 0; i < response.Sources.Count; i++)
            {
                var src = response.Sources[i].Chunk;
                sb.AppendLine($"{i + 1}. [{src.Title}]({src.Url}) `{src.DataSourceName}` " +
                              $"*(chunk {src.ChunkIndex + 1}/{src.TotalChunks})*");
            }
        }

        return sb.ToString();
    }
}

/// <summary>Per-agent configuration: which RAG system names this agent queries.</summary>
public class SharePointRagAgentOptions
{
    public const string SectionName = "SharePointRagAgent";
    public List<string> SystemNames { get; set; } = ["General"];
}
