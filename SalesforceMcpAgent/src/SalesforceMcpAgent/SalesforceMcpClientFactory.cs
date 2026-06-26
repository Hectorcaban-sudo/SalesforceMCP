using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace SalesforceMcpAgent;

/// <summary>
/// Builds an <see cref="IMcpClient"/> that connects to the Salesforce Hosted MCP
/// Server over Streamable HTTP (SSE transport).
///
/// Salesforce requires an OAuth 2.0 Bearer token in the Authorization header.
/// The <see cref="SalesforceTokenProvider"/> handles token acquisition and caching.
/// </summary>
public static class SalesforceMcpClientFactory
{
    /// <summary>
    /// Creates and initialises an MCP client connected to the Salesforce Hosted MCP Server.
    /// </summary>
    /// <param name="options">Salesforce connection options.</param>
    /// <param name="tokenProvider">Handles OAuth token retrieval.</param>
    /// <param name="loggerFactory">Optional logger factory for MCP client diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<IMcpClient> CreateAsync(
        SalesforceOptions options,
        SalesforceTokenProvider tokenProvider,
        ILoggerFactory? loggerFactory = null,
        CancellationToken ct = default)
    {
        var accessToken = await tokenProvider.GetAccessTokenAsync(ct);

        // The Salesforce MCP server URL is an SSE endpoint.
        // We pass the Bearer token as a custom request header.
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint     = new Uri(options.McpServerUrl),
            Name         = "Salesforce MCP",
            // Inject the Authorization header for every MCP request.
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}"
            }
        };

        var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(transportOptions),
            loggerFactory: loggerFactory,
            cancellationToken: ct);

        return mcpClient;
    }
}
