# SharePoint RAG — Universal Multi-Source RAG System
### .NET 10 · Microsoft.Agents.AI · Azure OpenAI · SharpCoreDB.VectorSearch

---

## Overview

Index **any combination of data sources** into isolated, encrypted vector indexes
and expose them as searchable RAG systems. Each agent declares which systems it
searches; queries fan out across all assigned systems and merge results.

```
Data Sources (any type)          Systems (HNSW indexes)         Agents
───────────────────────          ──────────────────────         ──────
SharePoint Online      ──┐
SQL / Deltek Costpoint ──┼──────► General         ────────────► SharePointRagAgent
Excel / CSV            ──┘

SharePoint PPQs        ──────────► PastPerformance ─┐
                                                      ├──────────► PastPerformanceAgent
SharePoint Proposals   ──────────► ProposalArchive ─┘

Deltek Costpoint DB    ──┐
Excel Contract Matrix  ──┼──────► Contracts       ────────────► (REST or future agent)
Deltek Vantagepoint    ──┘

PostgreSQL CRM         ──┐
Deltek Vantagepoint    ──┼──────► BdPipeline      ────────────► (future BdAgent)
                          ┘

CSV Skills Matrix      ──────────► HR              ────────────► (future HrAgent)

All of the above       ──────────► AllContent      ────────────► REST ?systems=AllContent
```

---

## Supported Connector Types

| Type | Class | What it ingests |
|---|---|---|
| `SharePoint` | `SharePointConnector` | SharePoint Online document libraries via MS Graph |
| `SqlDatabase` | `SqlConnector` | Any SQL database via ADO.NET (SQL Server, PostgreSQL, MySQL, SQLite) |
| `Excel` | `ExcelConnector` | `.xlsx` files (via ClosedXML) and `.csv` files; glob path patterns |
| `Deltek` | `DeltekConnector` | Deltek Vantagepoint REST API — Projects, Employees, Clients, Opportunities |
| `Custom` | `CustomConnectorBase` | Any source — implement the interface, register the factory |

All connectors emit `SourceRecord` objects. The pipeline, vector store, and agents are **completely unaware** of which connector produced a chunk.

---

## Quick Start

### 1. Configure your data sources and systems

```json
"RagRegistry": {
  "DataSources": [
    {
      "Name": "MySharePoint", "Type": "SharePoint",
      "Properties": {
        "SiteUrl":     "https://contoso.sharepoint.com/sites/Corp",
        "LibraryName": "Documents"
      }
    },
    {
      "Name": "MyDatabase", "Type": "SqlDatabase",
      "Properties": {
        "ConnectionString": "Server=sql01;Database=Projects;Integrated Security=true",
        "Query":            "SELECT Id, Title, Description AS Content FROM dbo.Projects",
        "IdColumn": "Id", "ContentColumn": "Content", "TitleColumn": "Title"
      }
    }
  ],
  "Systems": [
    {
      "Name": "General",
      "DataSourceNames": ["MySharePoint", "MyDatabase"],
      "TopK": 5, "MinScore": 0.5
    }
  ]
}
```

### 2. Configure user secrets

```bash
dotnet user-secrets set "SharpCoreDB:EncryptionPassword" "strong-random-password"
dotnet user-secrets set "AzureOpenAI:ApiKey" "your-key"
```

### 3. Provision, index, and query

```bash
dotnet run --project src/SharePointRag.Api

# Provision all system schemas
curl -X POST http://localhost:5000/api/index/provision

# Test all data source connections
curl -X POST http://localhost:5000/api/index/test-connections

# Full index (all systems)
curl -X POST http://localhost:5000/api/index/full

# Full index (one system only)
curl -X POST "http://localhost:5000/api/index/full?system=Contracts"

# Query
curl -X POST http://localhost:5000/api/rag/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "What IT contracts do we have with DoD agencies?"}'

# Query specific systems
curl -X POST "http://localhost:5000/api/rag/ask?systems=PastPerformance&systems=Contracts" \
  -H "Content-Type: application/json" \
  -d '{"question": "Find similar IT modernisation past performance"}'
```

---

## Connector Configuration Reference

### SharePoint

```json
{
  "Name": "MySite", "Type": "SharePoint",
  "CrawlParallelism": 4, "DeltaSupported": true,
  "Properties": {
    "SiteUrl":           "https://contoso.sharepoint.com/sites/MyLib",
    "LibraryName":       "Documents",
    "TenantId":          "",           // empty = use GlobalGraph
    "ClientId":          "",           // empty = use GlobalGraph
    "ClientSecret":      "",           // empty = use GlobalGraph
    "AllowedExtensions": ".pdf,.docx,.xlsx",
    "MaxFileSizeMb":     "50",
    "RootFolderPath":    "FY2020"      // optional sub-folder
  }
}
```

### SQL Database

```json
{
  "Name": "MyDB", "Type": "SqlDatabase",
  "CrawlParallelism": 4, "DeltaSupported": true,
  "Properties": {
    "ConnectionString":  "Server=sql01;Database=DB;Integrated Security=true",
    "Provider":          "SqlServer",     // SqlServer | Postgres | MySql | Sqlite
    "Query":             "SELECT Id, Title, Body AS Content, Url, UpdatedAt AS ModifiedAt FROM dbo.Articles WHERE Active=1",
    "IdColumn":          "Id",
    "ContentColumn":     "Content",
    "TitleColumn":       "Title",
    "UrlColumn":         "Url",
    "ModifiedAtColumn":  "ModifiedAt",
    "DeltaFilter":       "UpdatedAt > @since"
  }
}
```

**Required NuGet packages per provider:**

| Provider | Package |
|---|---|
| SQL Server | `Microsoft.Data.SqlClient` (included in Core) |
| PostgreSQL | `dotnet add package Npgsql` |
| MySQL | `dotnet add package MySqlConnector` |
| SQLite | `dotnet add package Microsoft.Data.Sqlite` |

### Excel / CSV

```json
{
  "Name": "MySpreadsheet", "Type": "Excel",
  "CrawlParallelism": 2, "DeltaSupported": true,
  "Properties": {
    "FilePaths":     "/data/contracts/*.xlsx,/data/awards.csv",
    "SheetName":     "Contracts",        // Excel only; empty = first sheet
    "ContentColumn": "Description",      // column name or 0-based index
    "TitleColumn":   "ContractNumber",
    "UrlColumn":     "SharePointLink",
    "HeaderRow":     "0"
  }
}
```

**Required NuGet package for Excel:**
```bash
dotnet add src/SharePointRag.Core package ClosedXML
```

### Deltek Vantagepoint

```json
{
  "Name": "Deltek", "Type": "Deltek",
  "CrawlParallelism": 4, "DeltaSupported": true,
  "Properties": {
    "BaseUrl":  "https://yourfirm.deltekfirst.com/VantagePoint/api/v1",
    "ApiKey":   "Bearer <TOKEN>",
    "Entities": "Projects,Clients,Employees,Opportunities",
    "Filter":   "ProjectStatus eq 'Active'",
    "PageSize": "200"
  }
}
```

### Custom Connector

```json
{
  "Name": "MyCRM", "Type": "Custom",
  "CrawlParallelism": 4, "DeltaSupported": false,
  "Properties": {
    "CustomType": "MyCompany.Connectors.CrmConnector, MyCompany.App",
    "BaseUrl":    "https://crm.internal/api",
    "ApiKey":     "<TOKEN>"
  }
}
```

Implement `CustomConnectorBase` and register:

```csharp
// In Program.cs
services.AddCustomConnector<CrmConnector, CrmConnectorFactory>();
```

---

## Adding a New Connector Type

1. Create a class implementing `IDataSourceConnector` (or extend `CustomConnectorBase`)
2. Create a factory implementing `IDataSourceConnectorFactory`
3. Register in DI:

```csharp
services.AddCustomConnector<BoxConnector, BoxConnectorFactory>();
```

4. Add a data source entry in `appsettings.json` with `"Type": "Custom"` and `"CustomType": "FullyQualified.TypeName"`

The pipeline, vector store, registry, and agents need **zero changes**.

---

## API Reference

### Index management

| Method | Path | Description |
|---|---|---|
| `GET`  | `/api/index/status` | Runtime status of all systems + data source health |
| `GET`  | `/api/index/registry` | Static definition of all systems + data sources (credentials masked) |
| `POST` | `/api/index/test-connections` | Test connectivity to all data sources (or `?system=X`) |
| `POST` | `/api/index/provision` | Create SharpCoreDB schema + HNSW index (all or `?system=X`) |
| `POST` | `/api/index/full` | Full re-index (all or `?system=X`) |
| `POST` | `/api/index/delta` | Delta re-index; falls back to full for sources with `DeltaSupported=false` |

### General RAG

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/rag/ask` | Search all systems |
| `POST` | `/api/rag/ask?systems=A&systems=B` | Search specific systems |

### Past Performance

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/pastperformance/ask` | Intent-aware query |
| `POST` | `/api/pastperformance/volume` | Draft proposal volume |
| `POST` | `/api/pastperformance/contracts/search` | Structured contract search |
| `GET`  | `/api/pastperformance/cpars` | CPARS ratings table |
| `POST` | `/api/pastperformance/gaps` | Gap analysis |

### Bot endpoints

| Method | Path | Bot |
|---|---|---|
| `POST` | `/api/messages` | SharePoint RAG bot |
| `POST` | `/api/pastperformance/messages` | Past Performance specialist bot |

---

## License

MIT
