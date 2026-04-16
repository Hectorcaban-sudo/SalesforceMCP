using PnP.Core.Services;
using PnP.GccHighDemo.Services;

namespace PnP.GccHighDemo;

/// <summary>
/// Extension methods for configuring PnP Core with different authentication methods
/// </summary>
public static class ProgramExtensions
{
    /// <summary>
    /// Adds PnP Core with ClientSecret authentication for GCC High
    /// 
    /// WARNING: This configuration ONLY works for Microsoft Graph API calls.
    /// SharePoint REST APIs require certificate-based authentication.
    /// Use this ONLY if you need Graph-only functionality.
    /// </summary>
    public static IServiceCollection AddPnPCoreWithClientSecret(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var clientId = configuration["Authentication:ClientId"] 
            ?? throw new InvalidOperationException("Authentication:ClientId not configured");
        var clientSecret = configuration["Authentication:ClientSecret"] 
            ?? throw new InvalidOperationException("Authentication:ClientSecret not configured");
        var tenantId = configuration["Authentication:TenantId"] 
            ?? throw new InvalidOperationException("Authentication:TenantId not configured");
        var siteUrl = configuration["PnPCore:Sites:TargetSite:SiteUrl"] 
            ?? throw new InvalidOperationException("Site URL not configured");

        // Add PnP Core services
        services.AddPnPCore(options =>
        {
            options.Environment = Microsoft365Environment.USGovernmentHigh;
            options.PnPContext.GraphFirst = true;
            options.PnPContext.GraphCanUseBeta = true;
            
            options.Sites.Add("TargetSite", new PnPCoreSiteOptions
            {
                SiteUrl = siteUrl
            });
        });

        // Register custom authentication provider
        services.AddSingleton<IAuthenticationProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ClientSecretAuthenticationProvider>>();
            return new ClientSecretAuthenticationProvider(clientId, clientSecret, tenantId, logger);
        });

        // Add PnP Core Authentication (minimal config since we use custom provider)
        services.AddPnPCoreAuthentication(options =>
        {
            options.Sites.Add("TargetSite", new PnPCoreAuthenticationSiteOptions
            {
                AuthenticationProviderName = "ClientSecretProvider"
            });
        });

        return services;
    }

    /// <summary>
    /// Adds PnP Core with X509 Certificate authentication for GCC High
    /// This is the RECOMMENDED approach for full SharePoint and Graph API access.
    /// </summary>
    public static IServiceCollection AddPnPCoreWithCertificate(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var clientId = configuration["PnPCore:Credentials:Configurations:CertificateAuth:ClientId"] 
            ?? throw new InvalidOperationException("ClientId not configured");
        var tenantId = configuration["PnPCore:Credentials:Configurations:CertificateAuth:TenantId"] 
            ?? throw new InvalidOperationException("TenantId not configured");
        var thumbprint = configuration["PnPCore:Credentials:Configurations:CertificateAuth:X509Certificate:Thumbprint"] 
            ?? throw new InvalidOperationException("Certificate thumbprint not configured");
        var siteUrl = configuration["PnPCore:Sites:TargetSite:SiteUrl"] 
            ?? throw new InvalidOperationException("Site URL not configured");

        // Add PnP Core services
        services.AddPnPCore(options =>
        {
            options.Environment = Microsoft365Environment.USGovernmentHigh;
            options.PnPContext.GraphFirst = true;
            options.PnPContext.GraphCanUseBeta = true;
            
            options.HttpRequests.UserAgent = "ISV|YourCompany|PnPGccHighDemo";
            
            options.Sites.Add("TargetSite", new PnPCoreSiteOptions
            {
                SiteUrl = siteUrl
            });
        });

        // Add PnP Core Authentication with certificate
        services.AddPnPCoreAuthentication(options =>
        {
            options.Credentials.Configurations.Add("CertificateAuth",
                new PnPCoreAuthenticationCredentialConfigurationOptions
                {
                    ClientId = clientId,
                    TenantId = tenantId,
                    X509Certificate = new PnPCoreAuthenticationX509CertificateOptions
                    {
                        StoreName = StoreName.My,
                        StoreLocation = StoreLocation.CurrentUser,
                        Thumbprint = thumbprint
                    }
                });
            
            options.Credentials.DefaultConfiguration = "CertificateAuth";
            
            options.Sites.Add("TargetSite",
                new PnPCoreAuthenticationSiteOptions
                {
                    AuthenticationProviderName = "CertificateAuth"
                });
        });

        return services;
    }
}