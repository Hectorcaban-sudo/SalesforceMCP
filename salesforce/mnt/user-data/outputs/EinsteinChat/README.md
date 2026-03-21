# EinsteinChat — Lightning Web Component

A Salesforce LWC that connects to your ASP.NET Core `/api/chat` endpoint.

## Structure

```
EinsteinChat/
├── sfdx-project.json
└── force-app/main/default/lwc/einsteinChat/
    ├── einsteinChat.html          # Template
    ├── einsteinChat.js            # Controller
    ├── einsteinChat.css           # Salesforce Lightning styles
    └── einsteinChat.js-meta.xml  # Metadata — exposes to App Builder
```

---

## Deploy to Salesforce

### Prerequisites
- Salesforce CLI (`sf` or `sfdx`) installed
- A Salesforce org (Developer, Sandbox, or Scratch org)

### Steps

```bash
# 1. Authenticate
sf org login web --alias my-org

# 2. Deploy the component
sf project deploy start --source-dir force-app --target-org my-org

# 3. Open your org
sf org open --target-org my-org
```

Then in Salesforce:
1. Go to **Setup → App Builder**
2. Open any App, Record, or Home page
3. Drag **Einstein Chat** from the component panel onto the page
4. Save and Activate

---

## Calling your ASP.NET API

LWC runs in the browser, so calling an external API requires one of:

### Option A — Named Credential + Apex proxy (recommended for production)

1. In Setup → **Named Credentials**, create a credential pointing to your API host
2. Create an Apex class to proxy the call:

```apex
@RestResource(urlMapping='/chat/*')
global class ChatProxy {
    @HttpPost
    global static String doPost(String body) {
        HttpRequest req = new HttpRequest();
        req.setEndpoint('callout:YourNamedCredential/api/chat');
        req.setMethod('POST');
        req.setHeader('Content-Type', 'application/json');
        req.setBody(body);
        HttpResponse res = new Http().send(req);
        return res.getBody();
    }
}
```

3. Update `API_ENDPOINT` in `einsteinChat.js` to `/services/apexrest/chat`

### Option B — Direct fetch with CORS (dev / internal tools)

Enable CORS on your ASP.NET app:

```csharp
// Program.cs
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("https://YOUR-ORG.lightning.force.com")
     .AllowAnyMethod()
     .AllowAnyHeader()));

// ...
app.UseCors();
```

Then set `API_ENDPOINT` in `einsteinChat.js` to your full API URL:
```js
const API_ENDPOINT = 'https://your-api.azurewebsites.net/api/chat';
```

Also add the domain to **Setup → CSP Trusted Sites** in Salesforce.

---

## App Builder Property

The component exposes an **API Endpoint URL** property in App Builder so admins
can set the endpoint without touching code.
