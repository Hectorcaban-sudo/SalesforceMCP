# PnP.Core GCC High (USGovernmentHigh) ASP.NET Core Demo

This project demonstrates how to authenticate to SharePoint Online in a GCC High (US Government Cloud High) environment using PnP.Core SDK.

## Important: Authentication Methods

### Certificate-Based Authentication (Recommended for SharePoint)

**For SharePoint REST API access, certificate-based authentication is REQUIRED.** ClientSecret alone will NOT work for SharePoint REST APIs because:
- SharePoint REST/CSOM APIs only support certificate-based authentication with Azure AD apps
- ClientSecret works only for Microsoft Graph API calls
- PnP.Core SDK uses both SharePoint REST and Microsoft Graph APIs

### Configuration Steps

1. **Register an Azure AD Application** in your GCC High tenant:
   - Navigate to https://portal.azure.us (GCC High portal)
   - Go to Azure Active Directory → App registrations → New registration
   - Note the Application (client) ID and Directory (tenant) ID

2. **Create a self-signed certificate**:
   ```powershell
   # Using PowerShell
   $cert = New-SelfSignedCertificate -Subject "CN=PnPCoreApp" -CertStoreLocation "Cert:\CurrentUser\My" -FriendlyName "PnP Core Demo" -KeySpec Signature -KeyExportPolicy Exportable -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256
   
   # Export certificate
   Export-Certificate -Cert $cert -FilePath "PnPCoreApp.cer"
   
   # Get thumbprint
   $cert.Thumbprint
   ```

3. **Upload certificate to Azure AD App**:
   - Go to Certificates & secrets in your app registration
   - Upload the .cer file

4. **Add API Permissions** (Application permissions for app-only access):
   - SharePoint → Sites.Selected (or Sites.FullControl.All)
   - Microsoft Graph → Sites.Read.All
   - Grant admin consent

5. **Update appsettings.json**:
   ```json
   {
     "PnPCore": {
       "Environment": "USGovernmentHigh",
       "Credentials": {
         "Configurations": {
           "CertificateAuth": {
             "ClientId": "your-client-id",
             "TenantId": "your-tenant-id",
             "X509Certificate": {
               "Thumbprint": "your-certificate-thumbprint"
             }
           }
         }
       },
       "Sites": {
         "TargetSite": {
           "SiteUrl": "https://yourtenant.sharepoint.us/sites/yoursite"
         }
       }
     }
   }
   ```

## GCC High Endpoints

When using `Environment: "USGovernmentHigh"`, PnP.Core automatically configures:
- Azure AD Login: `https://login.microsoftonline.us`
- Microsoft Graph: `https://graph.microsoft.us`
- SharePoint: `*.sharepoint.us`

## Running the Application

```bash
cd PnP.GccHighDemo
dotnet restore
dotnet run
```

The API will be available at:
- https://localhost:5001
- Swagger UI: https://localhost:5001/swagger

## API Endpoints

| Endpoint | Description |
|----------|-------------|
| GET /api/sharepoint/site-title | Get the SharePoint site title |
| GET /api/sharepoint/lists | Get all lists from the site |

## Alternative: OnBehalfOf Authentication (Delegated)

If you need to use ClientSecret for delegated scenarios (acting on behalf of a user):

```csharp
builder.Services.AddPnPCoreAuthentication(options =>
{
    options.Credentials.Configurations.Add("OnBehalfOfAuth",
        new PnPCoreAuthenticationCredentialConfigurationOptions
        {
            ClientId = configuration["ClientId"],
            TenantId = configuration["TenantId"],
            OnBehalfOf = new PnPCoreAuthenticationOnBehalfOfOptions
            {
                ClientSecret = configuration["ClientSecret"]
            }
        });
});
```

Note: OnBehalfOf requires a user token from the calling application - it's designed for middle-tier APIs.

## Singleton Pattern

The `IPnPContextFactory` is registered as a singleton by PnP.Core. The `SharePointService` is also registered as singleton, creating PnPContext instances on demand via the factory. This is the recommended pattern for ASP.NET Core applications.

## Troubleshooting

1. **"AADSTS500011: The resource principal was not found"**
   - Verify you're using the correct GCC High endpoints
   - Check that your site URL ends with `.sharepoint.us`

2. **Authentication failures**
   - Ensure certificate is properly installed in CurrentUser\My store
   - Verify the thumbprint matches
   - Check API permissions and admin consent

3. **403 Forbidden errors**
   - Verify Sites.Selected or appropriate permissions are granted
   - Ensure admin consent was provided for application permissions