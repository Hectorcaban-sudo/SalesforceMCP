# Salesforce CRM Agent  
### Microsoft.Agents.AI + Salesforce Hosted MCP Server  

An AI agent built with the **Microsoft Agent Framework** (`Microsoft.Agents.AI`) that connects to a **Salesforce Hosted MCP Server** as its tool provider.  

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                   Program.cs (REPL)                 │
│                                                     │
│  User input ──► SalesforceCrmAgent.ChatStreamingAsync│
└────────────────────────┬────────────────────────────┘
                         │
                   Microsoft.Agents.AI
                   AIAgent.RunStreamingAsync
                         │ (tool call)
                         ▼
┌─────────────────────────────────────────────────────┐
│           ModelContextProtocol IMcpClient            │
│                 (McpClient / SSE transport)          │
└────────────────────────┬────────────────────────────┘
                         │  HTTPS + Bearer token
                         ▼
┌─────────────────────────────────────────────────────┐
│        Salesforce Hosted MCP Server (SSE)            │
│   https://<org>.my.salesforce.com/api/mcp/<server>  │
│                                                     │
│  Tools discovered at runtime (e.g. query_sobject,   │
│  soql_query, invoke_flow, …)                        │
└─────────────────────────────────────────────────────┘
```

---

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 9 SDK | `dotnet --version` |
| Azure OpenAI resource | Any deployment: `gpt-4o`, `gpt-4o-mini`, etc. |
| Salesforce org | Enterprise Edition or above |
| Salesforce user | System Administrator or equivalent |

---

## Salesforce Setup (one-time)

### 1 – Enable a Hosted MCP Server

1. In Salesforce **Setup**, search for **MCP Servers**.  
2. Click the **Salesforce Servers** tab.  
3. Click the server you want (e.g. *SObject – All*) → **Activate**.  
4. Copy the **Server URL** (e.g. `https://YOUR_ORG.my.salesforce.com/api/mcp/sobject-all/sse`).

### 2 – Create an External Client App (ECA)

> **Note:** Connected Apps are *not* supported for MCP authentication. You must use an External Client App.

1. Setup → **External Client App Manager** → **New External Client App**.  
2. Fill in **Basic Information** (name, contact email).  
3. Expand **API (Enable OAuth Settings)** → enable OAuth.  
4. **Callback URL** – for a server/headless client use any valid HTTPS URL you control (the Client-Credentials flow doesn't redirect).  
5. **Selected OAuth Scopes** – add:  
   - `Manage user data via APIs (api)`  
   - `Access the identity URL service (id, profile, email, address, phone)`  
   - `Access MCP servers (mcp)` *(this scope unlocks MCP)*  
6. Enable **Require Secret for Web Server Flow**.  
7. Save → copy the **Consumer Key** and generate + copy the **Consumer Secret**.

### 3 – Configure the agent

Edit **`appsettings.local.json`** (never committed):

```jsonc
{
  "Salesforce": {
    "InstanceUrl":   "https://YOUR_ORG.my.salesforce.com",
    "McpServerUrl":  "https://YOUR_ORG.my.salesforce.com/api/mcp/sobject-all/sse",
    "ClientId":      "<Consumer Key>",
    "ClientSecret":  "<Consumer Secret>"
  },
  "AzureOpenAI": {
    "Endpoint":       "https://YOUR_RESOURCE.openai.azure.com/",
    "DeploymentName": "gpt-4o",
    "ApiKey":         "<Azure OpenAI API Key>"
  }
}
```

---

## Running the Agent

```bash
cd src/SalesforceMcpAgent
dotnet run
```

Sample session:

```
╔══════════════════════════════════════════════════════╗
║       Salesforce CRM Agent  (type 'exit' to quit)   ║
╚══════════════════════════════════════════════════════╝

You: Show me the top 5 open opportunities by amount
Agent: Here are the top 5 open opportunities …

You: How many new Cases were created this week?
Agent: Based on Salesforce data, 14 Cases were created since Monday …

You: exit
Goodbye!
```

---

## Project Structure

```
SalesforceMcpAgent/
├── src/SalesforceMcpAgent/
│   ├── Program.cs                   # Entry point, DI, REPL
│   ├── Options.cs                   # Typed config models
│   ├── SalesforceTokenProvider.cs   # OAuth token management
│   ├── SalesforceMcpClientFactory.cs# Builds IMcpClient with auth headers
│   ├── SalesforceCrmAgent.cs        # AIAgent wrapping all MCP tools
│   ├── appsettings.json             # Non-secret defaults
│   └── appsettings.local.json       # ← your secrets (git-ignored)
└── .gitignore
```

---

## Authentication Notes

### Headless / server-to-server (default)
This project uses the **OAuth 2.0 Client Credentials** flow via the ECA Consumer Key + Secret.  
Every MCP call is made as the service account's identity; Salesforce enforces its standard permission model.

### Interactive / per-user (PKCE)
For a desktop or CLI tool where a real user should authenticate, drive the **Authorization Code + PKCE** flow in a browser, obtain the access token, and paste it into `StaticAccessToken` in config — or implement the PKCE redirect loop in `SalesforceTokenProvider.FetchTokenAsync`.

### Quick testing with a static token
Set `StaticAccessToken` in config to a token you obtained from Postman or the Salesforce CLI (`sf org display --json`). The token provider will use it directly, skipping OAuth.

---

## Key NuGet Packages

| Package | Role |
|---|---|
| `Microsoft.Agents.AI` | Agent runtime, tool orchestration |
| `Microsoft.Agents.AI.OpenAI` | IChatClient bridge for Azure OpenAI |
| `ModelContextProtocol` | MCP C# SDK (client) |
| `ModelContextProtocol.HttpClient` | SSE / Streamable HTTP transport |
| `Azure.AI.OpenAI` | Azure OpenAI .NET SDK |
| `Azure.Identity` | `DefaultAzureCredential` |

---

## Extending the Agent

**Add another Salesforce MCP server** – activate a second server in Salesforce Setup (e.g. *Data Cloud SQL*), then call `SalesforceMcpClientFactory.CreateAsync` with the new URL and merge its tools into the agent's tool list.

**Add a custom .NET tool** – use `AIFunctionFactory.Create(MyStaticMethod)` and append it to `aiTools` in `SalesforceCrmAgent.CreateAsync`.

**Persist conversation history** – pass an `AgentSession` to `RunStreamingAsync` for multi-turn memory across calls.
