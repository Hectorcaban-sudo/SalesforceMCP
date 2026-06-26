using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace SalesforceMcpAgent;

/// <summary>
/// A CRM-focused AI agent that uses the Salesforce Hosted MCP Server as its
/// tool provider.
///
/// The agent is intentionally thin: it delegates all Salesforce operations
/// (querying records, running flows, calling Apex, etc.) to the MCP tools
/// discovered at runtime from the hosted server, keeping the agent code
/// decoupled from any particular Salesforce API version.
/// </summary>
public sealed class SalesforceCrmAgent
{
    private readonly AIAgent _agent;
    private readonly ILogger<SalesforceCrmAgent> _logger;

    private SalesforceCrmAgent(AIAgent agent, ILogger<SalesforceCrmAgent> logger)
    {
        _agent  = agent;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates the agent, connects to the Salesforce Hosted MCP Server, discovers
    /// available tools, and registers them with the underlying <see cref="AIAgent"/>.
    /// </summary>
    public static async Task<SalesforceCrmAgent> CreateAsync(
        IChatClient chatClient,
        IMcpClient mcpClient,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger<SalesforceCrmAgent>();

        // Discover tools exposed by the Salesforce MCP server at runtime.
        logger.LogInformation("Discovering tools from Salesforce Hosted MCP Server…");
        IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync(ct);

        logger.LogInformation("Discovered {Count} MCP tool(s): {Names}",
            mcpTools.Count,
            string.Join(", ", mcpTools.Select(t => t.Name)));

        // McpClientTool inherits from AIFunction / AITool, so it integrates
        // directly with the Microsoft Agent Framework tool registry.
        var aiTools = mcpTools.Cast<AITool>().ToList();

        // Build the AIAgent with an opinionated system prompt and all MCP tools.
        AIAgent agent = chatClient
            .AsBuilder()
            .UseLogging(loggerFactory)
            .Build()
            .AsAIAgent(
                name: "SalesforceCrmAgent",
                description: "A CRM assistant backed by Salesforce data via MCP.",
                instructions: """
                    You are a helpful Salesforce CRM assistant.
                    You have access to live Salesforce data through the tools provided.
                    
                    Guidelines:
                    - Always use a tool to retrieve data; never invent CRM records.
                    - When the user asks for accounts, contacts, opportunities, or cases,
                      use the appropriate Salesforce MCP tool (e.g. query_sobject or soql_query).
                    - Summarise results clearly and concisely.
                    - If a tool call fails, explain the error to the user and suggest next steps.
                    - Respect that you can only see records the authenticated user is permitted
                      to see; do not attempt to bypass Salesforce security.
                    """,
                tools: aiTools);

        return new SalesforceCrmAgent(agent, logger);
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>Sends a user message and returns the agent's full text reply.</summary>
    public async Task<string> ChatAsync(string userMessage, CancellationToken ct = default)
    {
        _logger.LogDebug("User → Agent: {Message}", userMessage);
        var reply = await _agent.RunAsync(userMessage, ct);
        _logger.LogDebug("Agent → User: {Reply}", reply);
        return reply;
    }

    /// <summary>Streams the agent's reply token-by-token to the console.</summary>
    public async Task ChatStreamingAsync(string userMessage, CancellationToken ct = default)
    {
        _logger.LogDebug("User → Agent (streaming): {Message}", userMessage);
        Console.Write("Agent: ");

        await foreach (var update in _agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, userMessage)], ct))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent text)
                    Console.Write(text.Text);
            }
        }

        Console.WriteLine();
    }
}
