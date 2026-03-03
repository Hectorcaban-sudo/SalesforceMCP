# Salesforce MCP Server + Python LangChain Clients

A complete solution for interacting with Salesforce data using natural language, built on:
- **C# MCP Server** — exposes Salesforce CRUD + NLP + CSV export as MCP tools
- **Schema-driven** — no hardcoded objects or fields; everything defined in JSON files
- **Python LangChain Agent** — autonomous multi-step task executor
- **Python LangChain Chatbot** — conversational assistant with session memory

---

## Architecture

```
┌───────────────────────────────────────┐
│         Python LangChain Client       │
│  (salesforce_agent.py /               │
│   salesforce_chatbot.py)              │
│                                       │
│  LLM (Claude) ──► Tool Calls          │
└──────────────────────┬────────────────┘
                       │ stdio (MCP)
┌──────────────────────▼────────────────┐
│       C# MCP Server                   │
│  SalesforceMcpServer.exe              │
│                                       │
│  ┌──────────────┐ ┌─────────────────┐ │
│  │ NlpService   │ │ SchemaService   │ │
│  │ (NL→Query)   │ │ (JSON schemas)  │ │
│  └──────────────┘ └─────────────────┘ │
│  ┌─────────────────────────────────┐  │
│  │ SalesforceService               │  │
│  │ (REST API: SOQL, CRUD, CSV)     │  │
│  └─────────────────────────────────┘  │
└───────────────────────────────────────┘
                       │ HTTPS
┌──────────────────────▼────────────────┐
│       Salesforce REST API             │
│       (your org)                      │
└───────────────────────────────────────┘
```

---

## Project Structure

```
SalesforceMCP/
├── SalesforceMcpServer/
│   ├── SalesforceMcpServer.csproj
│   ├── Program.cs
│   ├── salesforce-config.json          ← Your Salesforce credentials
│   ├── Models/
│   │   └── Models.cs                   ← All data models
│   ├── Services/
│   │   ├── SchemaService.cs            ← JSON schema loader/resolver
│   │   ├── NlpService.cs               ← Natural language → ParsedQuery
│   │   └── SalesforceService.cs        ← Salesforce REST API client
│   └── Tools/
│       └── SalesforceTools.cs          ← MCP tool definitions
├── schemas/
│   ├── Account.json                    ← Account object schema
│   ├── Contact.json                    ← Contact object schema
│   ├── Opportunity.json                ← Opportunity object schema
│   ├── Lead.json                       ← Lead object schema
│   └── YourCustomObject.json           ← Add your own!
└── python_clients/
    ├── salesforce_agent.py             ← Autonomous LangChain agent
    ├── salesforce_chatbot.py           ← Conversational chatbot
    ├── requirements.txt
    └── .env.example
```

---

## Setup

### 1. Configure Salesforce Credentials

Edit `SalesforceMcpServer/salesforce-config.json`:

```json
{
  "instanceUrl": "https://yourorg.my.salesforce.com",
  "clientId": "YOUR_CONNECTED_APP_CLIENT_ID",
  "clientSecret": "YOUR_CONNECTED_APP_CLIENT_SECRET",
  "username": "your.user@company.com",
  "password": "yourPassword",
  "securityToken": "yourSecurityToken"
}
```

**Creating a Connected App in Salesforce:**
1. Setup → App Manager → New Connected App
2. Enable OAuth Settings
3. Add scopes: `api`, `refresh_token`
4. Copy Client ID and Client Secret

### 2. Add/Customize Schema Files

Schema files live in the `schemas/` directory. Each file defines one Salesforce object.

**Example: schemas/MyCustomObject.json**
```json
{
  "objectName": "My_Custom_Object__c",
  "label": "My Custom Object",
  "labelPlural": "My Custom Objects",
  "aliases": ["custom", "my object"],
  "fields": [
    {
      "fieldName": "Name",
      "label": "Name",
      "type": "string",
      "aliases": ["name", "title"],
      "required": true,
      "updateable": true,
      "createable": true,
      "filterable": true
    },
    {
      "fieldName": "Custom_Status__c",
      "label": "Status",
      "type": "picklist",
      "aliases": ["status", "state"],
      "required": false,
      "updateable": true,
      "createable": true,
      "filterable": true
    }
  ]
}
```

**Field types:** `string`, `email`, `phone`, `url`, `textarea`, `picklist`, `boolean`, `integer`, `double`, `currency`, `percent`, `date`, `datetime`, `id`, `reference`

### 3. Build the MCP Server

```bash
cd SalesforceMcpServer
dotnet restore
dotnet build

# For production / Python clients:
dotnet publish -c Release -o ./publish
```

The compiled binary will be at `SalesforceMcpServer/publish/SalesforceMcpServer`.

### 4. Set Up Python Environment

```bash
cd python_clients
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate

pip install -r requirements.txt

cp .env.example .env
# Edit .env with your ANTHROPIC_API_KEY and MCP_SERVER_PATH
```

---

## Running

### Python Agent (Autonomous Tasks)

```bash
cd python_clients
python salesforce_agent.py

# Demo mode (pre-defined tasks):
python salesforce_agent.py --demo
```

**Example interactions:**
```
You: Find all open opportunities over $50,000 sorted by amount descending
You: Create a new lead named John Smith at Acme Corp, email john@acme.com
You: Export all accounts in California to /tmp/ca_accounts.csv
You: Update opportunity 006XX0000012345 - set stage to Closed Won and amount to 75000
You: How many contacts were created this year?
```

### Python Chatbot (Conversational)

```bash
cd python_clients
python salesforce_chatbot.py

# Demo conversation:
python salesforce_chatbot.py --demo
```

**Special commands in chatbot:**
- `history` — show recent conversation history
- `clear` — clear conversation and start fresh
- `schema` — list available Salesforce objects
- `quit` / `exit` — exit the chatbot

---

## Available MCP Tools

| Tool | Description |
|------|-------------|
| `SalesforceNaturalLanguage` | Primary tool — query/create/update/delete/export using plain English |
| `SalesforceSoqlQuery` | Execute raw SOQL queries |
| `SalesforceCreateRecord` | Create a record with structured JSON field values |
| `SalesforceUpdateRecord` | Update a record by ID with JSON field values |
| `SalesforceDeleteRecord` | Delete a record by ID |
| `SalesforceExportCsv` | Query records and save to CSV file |
| `SalesforceListSchema` | List all loaded objects and their fields |
| `SalesforceDescribeObject` | Detailed description of one object's fields |
| `SalesforceReloadSchemas` | Hot-reload schema JSON files |

---

## Natural Language Examples

The NLP service understands these patterns:

**Queries:**
- "show me all accounts in Texas"
- "find contacts at Acme Corp"
- "list open opportunities over $100,000"
- "get the top 10 leads sorted by created date"
- "how many accounts are in the technology industry"

**Create:**
- "create a new account named TechCorp in San Francisco"
- "add a lead: Jane Doe at MegaCorp, email jane@megacorp.com"

**Update:**
- "update opportunity 006XX0000012345 set amount to 75000"
- "change contact 003XX0000012345 phone to 555-9876"

**Delete:**
- "delete lead 00QXX0000012345"
- "remove contact with id 003XX0000012345"

**Export:**
- "export all contacts to csv"
- "download open opportunities to /tmp/opps.csv"

---

## Extending with New Objects

1. Create `schemas/YourObject.json` with the object definition
2. Either restart the server or call `SalesforceReloadSchemas` tool
3. The object is immediately available for natural language queries

No code changes required.

---

## Security Notes

- Keep `salesforce-config.json` out of version control (add to `.gitignore`)
- Store credentials in environment variables for production
- The MCP server runs locally — Salesforce credentials never leave your machine
- Use a dedicated Connected App with minimal required permissions
