# SharePoint RAG — Universal Multi-Source RAG + GovCon Past Performance Agent
### .NET 10 · Microsoft.Agents.AI · Azure OpenAI · SharpCoreDB.VectorSearch

---

## Overview

A production-ready RAG platform that ingests **any combination of data sources** —
SharePoint, SQL databases, Deltek, Excel, or custom connectors — into isolated
encrypted vector indexes. The **Past Performance Agent** is source-aware: it
searches and extracts from all of them simultaneously, using the right strategy
per connector type.

---

## Architecture

```
Data Sources                  Extraction Strategy         RAG System
────────────                  ───────────────────         ──────────
SharePoint PPQs/CPARS  ──────► LLM extraction (GPT-4o) ──►┐
                                (free-text → ContractRecord) │
                                                             ├──► PastPerformance
Deltek Vantagepoint ─────────► Direct field mapping ────────►│    (searched by
Deltek Costpoint DB  ─────────► (ProjectNumber→ContractNum,  │    PastPerformanceAgent)
Excel Contract Matrix ────────►  Budget→ContractValue, etc.)  │
                                Enrichment LLM if needed ─────►┘
                                                             │
                                                             ├──► Contracts
SharePoint Corp Docs ─────────► LLM extraction ─────────────► General
```

### Extraction routing (per chunk)

```
IndexingPipeline stamps ConnectorType into every chunk's Metadata["ConnectorType"]
                │
                ▼
        ContractExtractor.ExtractAsync()
                │
                ├─ Metadata["ConnectorType"] ∈ {SqlDatabase, Deltek, Excel}
                │       │
                │       ├─ TryDirectMapping()        ← no LLM, maps known column names
                │       │   success → ContractRecord with source provenance
                │       │
                │       └─ LlmStructuredEnrichment() ← LLM fallback for complex data
                │
                └─ Metadata["ConnectorType"] ∈ {SharePoint, Custom}
                        │
                        └─ LlmDocumentExtraction()   ← full unstructured extraction
```

---

## Supported Data Sources

| Type | Connector | Extraction | Best for |
|---|---|---|---|
| `SharePoint` | `SharePointConnector` | LLM (narrative) | PPQs, CPARS printouts, proposal volumes |
| `SqlDatabase` | `SqlConnector` | Direct mapping + LLM enrichment | Costpoint, custom contract DBs |
| `Deltek` | `DeltekConnector` | Direct mapping + LLM enrichment | Vantagepoint Projects/Clients/Employees |
| `Excel` | `ExcelConnector` | Direct mapping + LLM enrichment | Award matrices, CPARS exports |
| `Custom` | `CustomConnectorBase` | Your choice | Any REST API, blob storage, CRM |

---

## Project Structure

```
SharePointRag/
├── src/
│   ├── SharePointRag.Core/
│   │   ├── Configuration/Options.cs        DataSourceDefinition, DataSourceType,
│   │   │                                   SharePointProps/SqlProps/ExcelProps/DeltekProps
│   │   ├── Connectors/
│   │   │   ├── ConnectorRegistry.cs        Factory dispatcher (last-registered wins)
│   │   │   ├── SharePointConnector.cs      MS Graph + text extraction inline
│   │   │   ├── SqlConnector.cs             ADO.NET (SQL Server/Postgres/MySQL/SQLite)
│   │   │   ├── ExcelConnector.cs           .xlsx (ClosedXML) + .csv (built-in)
│   │   │   ├── DeltekConnector.cs          Vantagepoint REST API OData paging
│   │   │   └── CustomConnector.cs          Base class + RestApiConnectorExample
│   │   ├── Interfaces/Interfaces.cs        IDataSourceConnector, IConnectorRegistry, ...
│   │   ├── Models/Models.cs                DocumentChunk carries ConnectorType in Metadata
│   │   └── Services/
│   │       ├── IndexingPipeline.cs         Stamps ConnectorType → Metadata["ConnectorType"]
│   │       ├── LibraryRegistry.cs          Builds connectors/stores/pipelines from config
│   │       ├── RagOrchestrator.cs          Multi-system fan-out + merge + re-rank
│   │       └── VectorStore.cs / IndexStateStore.cs / EmbeddingService.cs / TextChunker.cs
│   │
│   ├── SharePointRag.PastPerformance/
│   │   ├── Models/PastPerformanceModels.cs
│   │   │     ContractRecord:     DataSourceName, ConnectorType, SourceMetadata
│   │   │     PastPerformanceQuery: ConnectorTypeFilter, DataSourceFilter
│   │   │     PastPerformanceResponse: DataSourcesSearched
│   │   ├── Services/
│   │   │   ├── ContractExtractor.cs   ★ Source-aware routing:
│   │   │   │                            Structured → TryDirectMapping → LLM enrichment
│   │   │   │                            Document  → LLM extraction
│   │   │   ├── RelevanceScorer.cs     ConnectorTypeFilter/DataSourceFilter + source bonuses
│   │   │   ├── PastPerformanceOrchestrator.cs  DataSourcesSearched in every response
│   │   │   └── ProposalDrafter.cs     Narrative prompt includes connectorType + dataSource
│   │   ├── Prompts/PastPerformancePrompts.cs
│   │   │     ContractExtractionSystem       (documents)
│   │   │     StructuredEnrichmentSystem     (SQL / Deltek / Excel)
│   │   │     NarrativeDraftUserTemplate     (includes {connectorType}/{dataSourceName})
│   │   ├── PastPerformanceAgent.cs    /sources command, connector type in contract cards
│   │   └── Extensions/PastPerformanceServiceExtensions.cs
│   │
│   ├── SharePointRag.Api/
│   │   ├── Controllers/
│   │   │   ├── PastPerformanceController.cs
│   │   │   │     GET  /api/pastperformance/sources
│   │   │   │     POST /api/pastperformance/contracts/search?connectorTypes=&dataSources=
│   │   │   │     GET  /api/pastperformance/cpars?connectorTypes=
│   │   │   ├── IndexController.cs     POST /api/index/test-connections
│   │   │   └── RagController.cs       POST /api/rag/ask?systems=
│   │   └── Program.cs
│   │
│   └── SharePointRag.Indexer/
│       └── IndexerWorker.cs           Indexer:SystemFilter for sharding
│
└── teams-manifests/
    ├── general-rag/manifest.json
    └── past-performance/manifest.json
```

---

## Quick Start

### 1. Configure data sources

```json
"RagRegistry": {
  "DataSources": [
    {
      "Name": "MyPPDocs", "Type": "SharePoint",
      "Properties": { "SiteUrl": "https://...", "LibraryName": "PastPerformance" }
    },
    {
      "Name": "MyCostpoint", "Type": "SqlDatabase",
      "Properties": {
        "ConnectionString": "Server=sql01;Database=CPAS;Integrated Security=true",
        "Query": "SELECT PROJ_ID AS Id, PROJ_NAME AS Title, PROJ_DESC AS Content, ... FROM GV_PROJECTS",
        "IdColumn": "Id", "ContentColumn": "Content", "TitleColumn": "Title"
      }
    },
    {
      "Name": "MyDeltek", "Type": "Deltek",
      "Properties": {
        "BaseUrl": "https://firm.deltekfirst.com/VantagePoint/api/v1",
        "ApiKey":  "Bearer <TOKEN>", "Entities": "Projects,Clients"
      }
    },
    {
      "Name": "MyMatrices", "Type": "Excel",
      "Properties": { "FilePaths": "/data/contracts/*.xlsx", "ContentColumn": "Description" }
    }
  ],
  "Systems": [
    {
      "Name": "PastPerformance",
      "DataSourceNames": [ "MyPPDocs" ],
      "TopK": 8, "MinScore": 0.45
    },
    {
      "Name": "Contracts",
      "DataSourceNames": [ "MyCostpoint", "MyDeltek", "MyMatrices" ],
      "TopK": 10, "MinScore": 0.4
    }
  ]
}
```

### 2. Declare which systems the PP agent searches

```json
"PastPerformanceAgent": {
  "SystemNames": [ "PastPerformance", "Contracts" ]
}
```

This causes the agent to fan out across **both** systems simultaneously —
SharePoint PPQs and structured database records in a single query.

### 3. Provision, index, query

```bash
# Provision all systems
curl -X POST http://localhost:5000/api/index/provision

# Test all data source connections
curl -X POST http://localhost:5000/api/index/test-connections

# Full index (all sources)
curl -X POST http://localhost:5000/api/index/full

# Full index of structured sources only
curl -X POST "http://localhost:5000/api/index/full?system=Contracts"

# Ask the PP agent (searches SharePoint + SQL + Deltek + Excel)
curl -X POST http://localhost:5000/api/pastperformance/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "Find IT modernisation contracts similar to this FISMA SOW"}'

# Draft a volume (draws from all sources automatically)
curl -X POST http://localhost:5000/api/pastperformance/volume \
  -H "Content-Type: application/json" \
  -d '{"solicitationContext": "W912DQ-24-R-0041 Army IT modernisation"}'

# See which data sources feed the PP agent
curl http://localhost:5000/api/pastperformance/sources

# Filter to Deltek only
curl -X POST "http://localhost:5000/api/pastperformance/contracts/search" \
  -H "Content-Type: application/json" \
  -d '{"keywords":"cloud migration DoD","connectorTypes":["Deltek"]}'

# Filter to SQL databases only
curl -X POST "http://localhost:5000/api/pastperformance/contracts/search" \
  -H "Content-Type: application/json" \
  -d '{"sowDescription":"FISMA compliance","connectorTypes":["SqlDatabase"]}'
```

---

## How the PP Agent Handles Each Source Type

### SharePoint (PPQs, CPARS printouts, proposal volumes)
- Chunks stored with `ConnectorType=SharePoint`
- **LLM extraction**: GPT-4o reads free-text and extracts `ContractRecord` fields
- Best for: narrative rich data, CPARS adjective ratings in prose, full scope descriptions

### SQL Database (Deltek Costpoint, custom contract systems)
- Chunks stored with `ConnectorType=SqlDatabase`
- **Direct mapping**: known column names (`PROJ_ID`, `NAICS_CODE`, `CO_NAME`, etc.) → `ContractRecord`
- LLM enrichment only for complex or non-standard schemas
- Best for: authoritative contract numbers, dollar values, dates, CO contacts

### Deltek Vantagepoint (REST API)
- Chunks stored with `ConnectorType=Deltek`
- **Direct mapping**: `ProjectNumber`, `ClientName`, `Budget`, `NAICSCode` → `ContractRecord`
- Content builder synthesises rich text from all entity fields
- Best for: pipeline data, employee skills, client relationships

### Excel / CSV (award matrices, CPARS exports)
- Chunks stored with `ConnectorType=Excel`
- **Direct mapping**: spreadsheet column headers → `ContractRecord` fields
- Best for: structured contract registers, bulk CPARS rating exports

### Custom connectors
- Set `ConnectorType` in `SourceRecord.Metadata` to control routing
- Extend `CustomConnectorBase` and register with `services.AddCustomConnector<T, F>()`

---

## Column Naming for Direct Mapping (SQL / Excel)

For best results without LLM enrichment, use these column names in your SQL query or Excel headers:

| Column name(s) | Maps to |
|---|---|
| `ProjectNumber`, `ContractNumber`, `CONTRACT_NUM`, `PROJ_ID` | `ContractRecord.ContractNumber` |
| `AgencyName`, `ClientName`, `Client` | `ContractRecord.AgencyName` |
| `NAICSCode`, `NAICS` | `ContractRecord.NaicsCodes` |
| `ContractAmount`, `Budget`, `ContractValue` | `ContractRecord.ContractValue` |
| `FinalObligatedValue`, `TotalValue` | `ContractRecord.FinalObligatedValue` |
| `StartDate`, `START_DATE`, `BeginDate` | `ContractRecord.StartDate` |
| `EndDate`, `END_DATE`, `CompletionDate` | `ContractRecord.EndDate` |
| `ProjectStatus`, `Status` (contains "Active") | `ContractRecord.IsOngoing` |
| `ContractingOfficer`, `CO`, `CO_NAME` | `ContractRecord.ContractingOfficer` |
| `COEmail`, `ContractingOfficerEmail` | `ContractRecord.ContractingOfficerEmail` |
| `CPARSRatingOverall`, `OverallRating`, `Rating` | `ContractRecord.CPARSRatingOverall` |
| `ContractType`, `CONTRACT_TYPE` | `ContractRecord.ContractType` |
| `PerformingEntity`, `Contractor` | `ContractRecord.PerformingEntity` |

---

## Filtering by Source in Queries

The query parser automatically detects source mentions, or you can filter explicitly:

**Natural language (Teams / REST ask)**
```
"From Deltek, find active DoD projects over $5M"
"Search our SQL database for NAICS 541512 contracts"
"Find SharePoint PPQs for Army work in the last 3 years"
```

**REST API explicit filters**
```bash
# Connector type filter
POST /api/pastperformance/contracts/search
{ "connectorTypes": ["SqlDatabase", "Deltek"], "sowDescription": "cloud infrastructure" }

# Named data source filter
POST /api/pastperformance/contracts/search
{ "dataSources": ["DeltekVantagepoint"], "keywords": "active Army projects" }

# CPARS table from Deltek only
GET /api/pastperformance/cpars?connectorTypes=Deltek

# Federated search across all sources
POST /api/rag/ask?systems=AllContent
{ "question": "Find all IT contracts over $10M in the last 5 years" }
```

---

## API Reference

### Past Performance

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/pastperformance/ask` | Intent-aware query (all sources) |
| `POST` | `/api/pastperformance/volume` | Draft proposal volume (all sources) |
| `POST` | `/api/pastperformance/volume/download` | Download as .txt |
| `POST` | `/api/pastperformance/contracts/search` | Structured contract search with source filters |
| `GET`  | `/api/pastperformance/cpars?agency=&connectorTypes=` | CPARS ratings table |
| `POST` | `/api/pastperformance/gaps` | Gap analysis |
| `GET`  | `/api/pastperformance/sources` | List data sources feeding PP systems |

### Index management

| Method | Path | Description |
|---|---|---|
| `GET`  | `/api/index/status?system=` | Runtime status + data source health |
| `GET`  | `/api/index/registry` | Static system + source definitions |
| `POST` | `/api/index/test-connections?system=` | Test all data source connections |
| `POST` | `/api/index/provision?system=` | Create SharpCoreDB schemas |
| `POST` | `/api/index/full?system=` | Full re-index |
| `POST` | `/api/index/delta?system=` | Incremental re-index |

### General RAG

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/rag/ask?systems=A&systems=B` | Cross-system search |

---

## License

MIT
