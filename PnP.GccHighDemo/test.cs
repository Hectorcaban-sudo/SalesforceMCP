using Microsoft.Identity.Client;
using PnP.Core.Auth;
using PnP.Core.Services;

var builder = WebApplication.CreateBuilder(args);

var config       = builder.Configuration.GetSection("SharePoint");
var tenantId     = config["TenantId"]!;
var clientId     = config["ClientId"]!;
var clientSecret = config["ClientSecret"]!;
var siteUrl      = new Uri(config["SiteUrl"]!);

// GCC High authority and scope
var authority    = $"https://login.microsoftonline.us/{tenantId}";
var scope        = "https://yourtenant.sharepoint.us/.default"; // GCC High SharePoint scope

var confidentialApp = ConfidentialClientApplicationBuilder
    .Create(clientId)
    .WithClientSecret(clientSecret)
    .WithAuthority(authority)
    .Build();

builder.Services.AddPnPCore(options =>
{
    options.Environment = Microsoft.SharePoint.Client.EnvironmentType.USGovernmentHigh;

    options.DefaultAuthenticationProvider = new ExternalAuthenticationProvider(
        async (resource, scopes) =>
        {
            var result = await confidentialApp
                .AcquireTokenForClient(new[] { scope })
                .ExecuteAsync();
            return result.AccessToken;
        });
});

// No AddPnPCoreAuthentication needed — auth is handled above

// Singleton PnPContext
builder.Services.AddSingleton<PnPContext>(sp =>
{
    var factory = sp.GetRequiredService<IPnPContextFactory>();
    return factory.CreateAsync(siteUrl).GetAwaiter().GetResult();
});

builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();