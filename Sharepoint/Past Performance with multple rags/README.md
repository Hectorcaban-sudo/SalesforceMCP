# SharePoint RAG — Multi-Library Flexible RAG System
### .NET 10 · Microsoft.Agents.AI · Azure OpenAI · SharpCoreDB.VectorSearch

---

## What This Is

A production-ready RAG system that ingests **multiple SharePoint libraries** into
**multiple isolated vector indexes**, then lets each agent declare exactly which
indexes it searches. Libraries and indexes are completely decoupled:

```
Libraries (SharePoint sites)          Systems (HNSW indexes)         Agents
─────────────────────────────         ──────────────────────         ──────
GeneralDocs        ──────────────────► General         ────────────► SharePointRagAgent
                                                                      (searches: General)

PastPerformanceDocs ─┐
                      ├───────────────► PastPerformance ─┐
ProposalArchiveDocs ─┘                                    ├──────────► PastPerformanceAgent
                      ┌───────────────► ProposalArchive ─┘            (searches: both)
ProposalArchiveDocs ─┘

HRPolicies ──────────────────────────► HR              ────────────► (future HrAgent)

All four ─────────────────────────────► AllContent     ────────────► REST ?systems=AllContent
```

**Key rules:**
- A library can feed multiple systems (indexed independently into each).
- A system aggregates one or more libraries into a single isolated HNSW index.
- An agent declares its system names; search fans out across all of them in parallel.
- Indexes are completely isolated on disk: `{DataDirectory}/{systemName}/`.

---

## Architecture

```
appsettings.json
└── RagRegistry
    ├── Libraries[]          one entry per SharePoint library
    │   ├── Name             "PastPerformanceDocs"
    │   ├── SiteUrl          "https://contoso.sharepoint.com/sites/CaptureTeam"
    │   ├── LibraryName      "PastPerformance"
    │   ├── ClientId/Secret  (optional — falls back to GlobalGraph)
    │   └── AllowedExtensions, MaxFileSizeMb, CrawlParallelism, RootFolderPath
    │
    └── Systems[]            one entry per logical RAG index
        ├── Name             "PastPerformance"
        ├── LibraryNames     ["PastPerformanceDocs"]   ← which libraries feed this index
        ├── TopK             8
        └── MinScore         0.45

                │
                ▼
        ILibraryRegistry  (built at startup from config)
        ├── GetCrawler("PastPerformanceDocs")     → ISharePointCrawler
        ├── GetVectorStore("PastPerformance")     → IVectorStore  (SharpCoreDB HNSW)
        ├── GetPipeline("PastPerformance")        → IIndexingPipeline
        └── GetStateStore("PastPerformance")      → IIndexStateStore

                │
                ▼
        IRagOrchestratorFactory
        └── Create(["PastPerformance","ProposalArchive"])
                │
                ▼
        RagOrchestrator
        ├── Embed question
        ├── SearchAsync on "PastPerformance" store  ──┐
        ├── SearchAsync on "ProposalArchive"  store  ──┤ parallel
        └── Merge + re-rank by score ─────────────────┘
```

---

## Project Structure

```
SharePointRag/
├── src/
│   ├── SharePointRag.Core/
│   │   ├── Configuration/Options.cs
│   │   │     LibraryDefinition      — one SharePoint library
│   │   │     RagSystemDefinition    — one logical HNSW index
│   │   │     RagRegistryOptions     — root config (Libraries[] + Systems[])
│   │   │     GlobalGraphOptions     — fallback Graph credentials
│   │   │     SharpCoreDbOptions     — root data dir + encryption password
│   │   │
│   │   ├── Interfaces/Interfaces.cs
│   │   │     ISharePointCrawler     — per-library; carries LibraryName
│   │   │     IVectorStore           — per-system; SearchAsync(vector,topK,minScore)
│   │   │     IIndexingPipeline      — per-system; iterates all assigned libraries
│   │   │     IIndexStateStore       — per-system; keyed by library::driveItemId
│   │   │     ILibraryRegistry       — central runtime lookup
│   │   │     IRagOrchestrator       — multi-system fan-out
│   │   │     IRagOrchestratorFactory— creates orchestrators by system name list
│   │   │
│   │   ├── Models/Models.cs
│   │   │     DocumentChunk          — carries LibraryName provenance field
│   │   │     SharePointFile         — carries LibraryName
│   │   │     IndexingRecord         — carries RagSystemName + LibraryName
│   │   │     RagSystemStatus        — runtime status response
│   │   │     LibraryStatus          — per-library status within a system
│   │   │
│   │   └── Services/
│   │         SharePointCrawler.cs   — takes LibraryDefinition; per-library credentials
│   │         VectorStore.cs         — SharpCoreDbVectorStore; isolated sub-directory
│   │         IndexStateStore.cs     — JsonFileIndexStateStore; keyed by library+driveId
│   │         IndexingPipeline.cs    — iterates all libraries assigned to its system
│   │         LibraryRegistry.cs     — ★ central factory; builds everything from config
│   │         RagOrchestrator.cs     — multi-system fan-out + merge + re-rank
│   │         EmbeddingService.cs    — Azure OpenAI batched (shared across all systems)
│   │         TextChunker.cs
│   │         TextExtractors.cs      — PDF, DOCX, TXT, HTML (pluggable)
│   │
│   ├── SharePointRag.Agent/
│   │   └── SharePointRagAgent.cs   — reads SharePointRagAgent.SystemNames from config
│   │
│   ├── SharePointRag.PastPerformance/
│   │   ├── Services/PastPerformanceOrchestrator.cs
│   │   │     — injects IRagOrchestratorFactory
│   │   │     — reads PastPerformanceAgent.SystemNames from config
│   │   │     — fans out across all assigned systems
│   │   └── Extensions/PastPerformanceServiceExtensions.cs
│   │         — exposes PastPerformanceAgentOptions
│   │
│   ├── SharePointRag.Api/
│   │   ├── Controllers/
│   │   │     IndexController.cs          — ?system= param; GET /api/index/registry
│   │   │     RagController.cs            — ?systems= param (multi-select)
│   │   │     PastPerformanceController.cs
│   │   └── Program.cs
│   │
│   └── SharePointRag.Indexer/
│       ├── IndexerWorker.cs         — Indexer:SystemFilter for selective indexing
│       └── Program.cs
│
├── teams-manifests/
│   ├── general-rag/manifest.json
│   └── past-performance/manifest.json
│
├── Dockerfile.api / Dockerfile.indexer
├── docker-compose.yml
└── .env.example
```

---

## Quick Start

### 1. Configure your libraries and systems

Edit `appsettings.json` under `RagRegistry`:

```json
"RagRegistry": {
  "Libraries": [
    {
      "Name":             "MyDocs",
      "SiteUrl":          "https://contoso.sharepoint.com/sites/Corp",
      "LibraryName":      "Documents",
      "AllowedExtensions":[ ".pdf", ".docx" ],
      "MaxFileSizeMb":    50,
      "CrawlParallelism": 4
    }
  ],
  "Systems": [
    {
      "Name":         "General",
      "LibraryNames": [ "MyDocs" ],
      "TopK":         5,
      "MinScore":     0.5
    }
  ]
}
```

### 2. Declare which systems each agent searches

```json
"SharePointRagAgent":    { "SystemNames": [ "General" ] },
"PastPerformanceAgent":  { "SystemNames": [ "PastPerformance", "ProposalArchive" ] }
```

### 3. Provision + index

```bash
# Start the API
dotnet run --project src/SharePointRag.Api

# Provision all systems (creates SharpCoreDB schema + HNSW indexes)
curl -X POST http://localhost:5000/api/index/provision

# Provision a single system only
curl -X POST "http://localhost:5000/api/index/provision?system=PastPerformance"

# Full index all systems
curl -X POST http://localhost:5000/api/index/full

# Full index one system
curl -X POST "http://localhost:5000/api/index/full?system=HR"
```

### 4. Query

```bash
# Search all systems (federated)
curl -X POST http://localhost:5000/api/rag/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "What is the travel expense policy?"}'

# Search specific systems only
curl -X POST "http://localhost:5000/api/rag/ask?systems=PastPerformance&systems=ProposalArchive" \
  -H "Content-Type: application/json" \
  -d '{"question": "Find IT modernisation contracts for DoD"}'

# Registry introspection
curl http://localhost:5000/api/index/registry
curl http://localhost:5000/api/index/status
curl "http://localhost:5000/api/index/status?system=PastPerformance"
```

---

## Configuration Reference

### `RagRegistry.Libraries[]`

| Field | Type | Description |
|---|---|---|
| `Name` | string | Unique key referenced by Systems |
| `SiteUrl` | string | Full SharePoint site URL |
| `LibraryName` | string | Document library name (e.g. "Documents") |
| `TenantId` | string | Override tenant (empty = use GlobalGraph) |
| `ClientId` | string | Override app registration (empty = use GlobalGraph) |
| `ClientSecret` | string | Override secret (empty = use GlobalGraph) |
| `AllowedExtensions` | string[] | File extensions to index (empty = all) |
| `MaxFileSizeMb` | int | Skip files larger than this |
| `CrawlParallelism` | int | Parallel download workers |
| `RootFolderPath` | string? | Restrict crawl to a sub-folder |

### `RagRegistry.Systems[]`

| Field | Type | Description |
|---|---|---|
| `Name` | string | Unique key used by agents and REST endpoints |
| `Description` | string | Human-readable description (appears in /api/index/registry) |
| `LibraryNames` | string[] | Libraries that feed this index |
| `TopK` | int | KNN neighbours to retrieve per query |
| `MinScore` | double | Minimum cosine similarity threshold (0–1) |

### Agent system binding

```json
"SharePointRagAgent":   { "SystemNames": ["General"] }
"PastPerformanceAgent": { "SystemNames": ["PastPerformance", "ProposalArchive"] }
```

### Indexer sharding

Run multiple indexer containers targeting different system subsets:

```yaml
indexer-pp:
  environment:
    Indexer__SystemFilter__0: "PastPerformance"
    Indexer__SystemFilter__1: "ProposalArchive"

indexer-general:
  environment:
    Indexer__SystemFilter__0: "General"
    Indexer__SystemFilter__1: "HR"
```

---

## REST API Reference

### Index management

| Method | Path | Description |
|---|---|---|
| `GET`  | `/api/index/status` | Status of all systems (or `?system=X`) |
| `GET`  | `/api/index/registry` | Full library + system definitions |
| `POST` | `/api/index/provision` | Create HNSW schema (all or `?system=X`) |
| `POST` | `/api/index/full` | Full re-index (all or `?system=X`) |
| `POST` | `/api/index/delta` | Delta re-index (all or `?system=X`) |

### General RAG

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/rag/ask` | Search all systems |
| `POST` | `/api/rag/ask?systems=A&systems=B` | Search specific systems |

### Past Performance

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/pastperformance/ask` | Intent-aware PP query |
| `POST` | `/api/pastperformance/volume` | Draft full proposal volume |
| `POST` | `/api/pastperformance/volume/download` | Download as .txt |
| `POST` | `/api/pastperformance/contracts/search` | Structured contract search |
| `GET`  | `/api/pastperformance/cpars?agency=DoD` | CPARS ratings table |
| `POST` | `/api/pastperformance/gaps` | Gap analysis |

### Bot endpoints

| Method | Path | Bot |
|---|---|---|
| `POST` | `/api/messages` | SharePoint RAG bot (Teams default) |
| `POST` | `/api/pastperformance/messages` | Past Performance specialist bot |

---

## Common Patterns

### Pattern 1 — One library, one system, one agent (simplest)
```json
"Libraries":  [{ "Name": "Corp", ... }],
"Systems":    [{ "Name": "General", "LibraryNames": ["Corp"] }],
"SharePointRagAgent": { "SystemNames": ["General"] }
```

### Pattern 2 — Two libraries feeding one PP system
```json
"Libraries":  [{ "Name": "PPQ" }, { "Name": "CPARSReports" }],
"Systems":    [{ "Name": "PastPerformance", "LibraryNames": ["PPQ","CPARSReports"] }],
"PastPerformanceAgent": { "SystemNames": ["PastPerformance"] }
```

### Pattern 3 — Same library in two systems (different TopK/MinScore)
```json
"Libraries":  [{ "Name": "Proposals" }],
"Systems":    [
  { "Name": "ProposalFast",  "LibraryNames": ["Proposals"], "TopK": 3, "MinScore": 0.7 },
  { "Name": "ProposalDeep",  "LibraryNames": ["Proposals"], "TopK": 15, "MinScore": 0.3 }
]
```

### Pattern 4 — PP Agent fans across two separate systems
```json
"Systems":    [
  { "Name": "PastPerformance", "LibraryNames": ["PPQ"] },
  { "Name": "ProposalArchive", "LibraryNames": ["Proposals"] }
],
"PastPerformanceAgent": { "SystemNames": ["PastPerformance","ProposalArchive"] }
// → Vector search fans out across both in parallel, results merged + re-ranked
```

### Pattern 5 — Federated system across all libraries
```json
{
  "Name": "AllContent",
  "LibraryNames": ["Corp","PPQ","Proposals","HR"],
  "TopK": 10, "MinScore": 0.4
}
// → REST only: POST /api/rag/ask?systems=AllContent
```

### Pattern 6 — Indexer sharding (scale-out)
```bash
# Container 1: index PP systems
Indexer__SystemFilter__0=PastPerformance
Indexer__SystemFilter__1=ProposalArchive

# Container 2: index everything else
Indexer__SystemFilter__0=General
Indexer__SystemFilter__1=HR
```

---

## License

MIT
