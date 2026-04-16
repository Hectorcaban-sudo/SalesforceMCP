using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using PnP.Core.Services;

namespace PnP.GccHighDemo.Services;

/// <summary>
/// Custom authentication provider using ClientSecret for GCC High
/// 
/// IMPORTANT WARNING: This authentication provider uses ClientSecret which only works
/// for Microsoft Graph API calls. SharePoint REST APIs require certificate-based authentication.
/// 
/// Use this provider ONLY if:
/// 1. You only need to access Microsoft Graph APIs
/// 2. You understand PnP.Core functionality will be limited
/// 
/// For full SharePoint REST API support, use X509Certificate authentication instead.
/// </summary>
public class ClientSecretAuthenticationProvider : IAuthenticationProvider
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tenantId;
    private readonly ILogger<ClientSecretAuthenticationProvider> _logger;
    
    // GCC High endpoints
    private const string Authority = "https://login.microsoftonline.us/";
    private const string GraphResource = "https://graph.microsoft.us/.default";
    private const string SharePointResource = "https://yourtenant.sharepoint.us/.default"; // Update with your tenant

    public ClientSecretAuthenticationProvider(
        string clientId,
        string clientSecret,
        string tenantId,
        ILogger<ClientSecretAuthenticationProvider> logger)
    {
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> GetAccessTokenAsync(Uri resource, string[] scopes)
    {
        _logger.LogInformation("Getting access token for resource: {Resource}", resource);
        
        // Build the authority URL for GCC High
        var authorityUrl = $"{Authority}{_tenantId}";
        
        // Create confidential client application for GCC High
        var app = ConfidentialClientApplicationBuilder.Create(_clientId)
            .WithClientSecret(_clientSecret)
            .WithAuthority(new Uri(authorityUrl))
            .Build();
        
        // Determine scopes based on resource
        var finalScopes = scopes.Length > 0 
            ? scopes 
            : new[] { GraphResource }; // Default to Graph
        
        try
        {
            var result = await app.AcquireTokenForClient(finalScopes)
                .ExecuteAsync();
            
            _logger.LogInformation("Successfully acquired access token");
            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            _logger.LogError(ex, "Failed to acquire access token. ErrorCode: {ErrorCode}", ex.ErrorCode);
            throw;
        }
    }

    public Task<string> GetAccessTokenAsync(Uri resource)
    {
        // Default to Graph scopes
        return GetAccessTokenAsync(resource, new[] { GraphResource });
    }
}