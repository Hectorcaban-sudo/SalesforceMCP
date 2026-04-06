using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Text;

namespace SharePointRag.Agent;

/// <summary>
/// SharePoint RAG Agent built on Microsoft.Agents SDK.
///
/// Handles every incoming message by running the full RAG pipeline
/// (embed question → vector search → grounded LLM answer) and
/// returns the answer with source citations.
///
/// Works with any channel supported by Microsoft.Agents (Teams, WebChat,
/// Direct Line, etc.).
/// </summary>
public sealed class SharePointRagAgent : AgentApplication
{
    private readonly IRagOrchestrator _rag;
    private readonly ILogger<SharePointRagAgent> _logger;

    public SharePointRagAgent(
        AgentApplicationOptions options,
        IRagOrchestrator rag,
        ILogger<SharePointRagAgent> logger)
        : base(options)
    {
        _rag    = rag;
        _logger = logger;

        // Register activity handlers
        OnActivity(ActivityTypes.Message,          OnMessageAsync);
        OnActivity(ActivityTypes.ConversationUpdate, OnConversationUpdateAsync);

        // Commands (slash-style)
        OnMessage("/help",   OnHelpAsync);
        OnMessage("/status", OnStatusAsync);
    }

    // ── Conversation welcome ──────────────────────────────────────────────────

    private async Task OnConversationUpdateAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        if (ctx.Activity.MembersAdded?.Any(m => m.Id != ctx.Activity.Recipient.Id) == true)
        {
            await ctx.SendActivityAsync(
                """
                👋 **Hello!** I'm the SharePoint Knowledge Assistant.

                Ask me anything about your documents. I'll search the library and provide
                grounded answers with source links.

                Type **/help** for usage tips.
                """, ct);
        }
    }

    // ── Main message handler ──────────────────────────────────────────────────

    private async Task OnMessageAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        var question = ctx.Activity.Text?.Trim();
        if (string.IsNullOrEmpty(question)) return;

        // Typing indicator
        await ctx.SendActivityAsync(
            Activity.CreateTypingActivity(), ct);

        try
        {
            var response = await _rag.AskAsync(question, ct);
            var reply    = FormatReply(response);
            await ctx.SendActivityAsync(reply, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG pipeline failed for question: {Q}", question);
            await ctx.SendActivityAsync(
                "⚠️ I encountered an error while searching the documents. Please try again.", ct);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task OnHelpAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        await ctx.SendActivityAsync(
            """
            **SharePoint Knowledge Assistant – Help**

            Simply type your question and I'll search the document library.

            **Commands**
            • `/help`   – Show this help message
            • `/status` – Show index statistics

            **Tips**
            • Be specific – e.g. "What is the refund policy for enterprise licenses?"
            • Ask follow-up questions naturally
            """, ct);
    }

    private async Task OnStatusAsync(
        ITurnContext ctx, ITurnState state, CancellationToken ct)
    {
        // In a real implementation you'd query the index for stats
        await ctx.SendActivityAsync(
            "📊 **Index status**: Connected to Azure AI Search. Use `/help` for usage tips.", ct);
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static string FormatReply(RagResponse response)
    {
        var sb = new StringBuilder();
        sb.AppendLine(response.Answer);

        if (response.Sources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("**Sources**");
            for (int i = 0; i < response.Sources.Count; i++)
            {
                var src = response.Sources[i].Chunk;
                sb.AppendLine($"{i + 1}. [{src.FileName}]({src.WebUrl}) *(chunk {src.ChunkIndex + 1}/{src.TotalChunks})*");
            }
        }

        return sb.ToString();
    }
}
