using System.Net.Http.Headers;
using System.Text.Json;

namespace SalesforceMcpAgent;

/// <summary>
/// Manages OAuth 2.0 access tokens for the Salesforce Hosted MCP Server.
///
/// Salesforce Hosted MCP uses OAuth 2.0 Authorization Code + PKCE (per-user).
/// For server / headless scenarios you can use the OAuth 2.0 Client Credentials
/// flow (Connected-App JWT Bearer) or a pre-obtained access token stored in
/// configuration.  This class supports both patterns:
///   1. A static pre-obtained token  (simplest for testing / dev)
///   2. Client-Credentials JWT Bearer flow  (service-to-service)
///
/// For interactive desktop / CLI flows you would drive the PKCE dance in a
/// browser; that wiring is left to the host application.
/// </summary>
public sealed class SalesforceTokenProvider
{
    private readonly SalesforceOptions _options;
    private readonly HttpClient _http;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public SalesforceTokenProvider(SalesforceOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// Returns a valid Bearer token, refreshing via Client-Credentials when needed.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // 1. Static token (pre-obtained, e.g. from a test session)
        if (!string.IsNullOrWhiteSpace(_options.StaticAccessToken))
            return _options.StaticAccessToken;

        // 2. Cached token still valid (with a 60-second buffer)
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry - TimeSpan.FromSeconds(60))
            return _cachedToken;

        // 3. Fetch a new token via JWT Bearer / Client-Credentials
        _cachedToken = await FetchTokenAsync(ct);
        return _cachedToken;
    }

    private async Task<string> FetchTokenAsync(CancellationToken ct)
    {
        // Salesforce OAuth token endpoint:  https://<instance>.salesforce.com/services/oauth2/token
        var tokenUrl = $"{_options.InstanceUrl.TrimEnd('/')}/services/oauth2/token";

        var body = new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(body)
        };

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("access_token missing in Salesforce response.");

        // Salesforce tokens typically last 2 hours; read issued_at if present.
        _tokenExpiry = DateTimeOffset.UtcNow.AddHours(2);

        return token;
    }

    /// <summary>
    /// Builds the Authorization header value for MCP requests.
    /// </summary>
    public async Task<AuthenticationHeaderValue> GetAuthHeaderAsync(CancellationToken ct = default)
        => new("Bearer", await GetAccessTokenAsync(ct));
}
