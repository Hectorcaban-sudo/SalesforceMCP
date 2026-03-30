# Salesforce SOQL Multi-Agent Sample

This project provides:

1. **ASP.NET Core Web API** with `Microsoft.SemanticKernel` and `GroupChatOrchestration`.
2. **Three domain agents** (`Accounts`, `Opportunities`, `Contracts`) that convert natural language into valid SOQL.
3. **Per-agent plugins** used to provide Salesforce object metadata and staged SOQL execution hooks.
4. **Lightning Web Component (LWC)** client under `force-app/main/default/lwc/salesforceAgentChat`.

## API contract

`POST /api/salesforce/agent-chat`

Request body is a **raw JSON array** of chat messages, preserving history:

```json
[
  { "role": "user", "content": "show top 5 opportunities closing this month" },
  { "role": "assistant", "content": "..." },
  { "role": "user", "content": "now only won deals" }
]
```

Response includes agent outputs and updated history.

## Run the API

```bash
dotnet run --project salesforce/ChatApp.csproj
```

## LWC deployment notes

- Component folder: `salesforce/force-app/main/default/lwc/salesforceAgentChat`
- Exposed property: `apiEndpoint` (set this in App Builder)
- Ensure your API host is reachable from Salesforce and CORS is configured.
