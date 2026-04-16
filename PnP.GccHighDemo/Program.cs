using PnP.Core.Services;
using PnP.GccHighDemo.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure PnP Core SDK for GCC High with certificate authentication
builder.Services.AddPnPCore(options =>
{
    // Set the environment to USGovernmentHigh (GCC High)
    options.Environment = Microsoft365Environment.USGovernmentHigh;
    
    options.PnPContext.GraphFirst = true;
    options.PnPContext.GraphCanUseBeta = true;
    
    options.HttpRequests.UserAgent = "ISV|YourCompany|PnPGccHighDemo";
    
    // Configure the target SharePoint site
    options.Sites.Add("TargetSite", new PnPCoreSiteOptions
    {
        SiteUrl = builder.Configuration["PnPCore:Sites:TargetSite:SiteUrl"] 
            ?? throw new InvalidOperationException("Target site URL not configured")
    });
});

// Configure PnP Core Authentication with certificate (required for SharePoint REST APIs)
builder.Services.AddPnPCoreAuthentication(options =>
{
    options.Credentials.Configurations.Add("CertificateAuth",
        new PnPCoreAuthenticationCredentialConfigurationOptions
        {
            ClientId = builder.Configuration["PnPCore:Credentials:Configurations:CertificateAuth:ClientId"] 
                ?? throw new InvalidOperationException("ClientId not configured"),
            TenantId = builder.Configuration["PnPCore:Credentials:Configurations:CertificateAuth:TenantId"] 
                ?? throw new InvalidOperationException("TenantId not configured"),
            X509Certificate = new PnPCoreAuthenticationX509CertificateOptions
            {
                StoreName = StoreName.My,
                StoreLocation = StoreLocation.CurrentUser,
                Thumbprint = builder.Configuration["PnPCore:Credentials:Configurations:CertificateAuth:X509Certificate:Thumbprint"] 
                    ?? throw new InvalidOperationException("Certificate thumbprint not configured")
            }
        });
    
    options.Credentials.DefaultConfiguration = "CertificateAuth";
    
    options.Sites.Add("TargetSite",
        new PnPCoreAuthenticationSiteOptions
        {
            AuthenticationProviderName = "CertificateAuth"
        });
});

// Register SharePointService as singleton - it will use the singleton IPnPContextFactory
builder.Services.AddSingleton<ISharePointService, SharePointService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();