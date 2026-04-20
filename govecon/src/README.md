# GovConRAG — Enterprise RAG Platform for Government Contractors

A full-stack enterprise AI platform built with **Microsoft Agent Framework (RC)**, **LiteGraph 5.x** (graph + vector store), **Wolverine MQTT** messaging, and **OpenAI** — designed for GovCon contractors managing 100K+ documents.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                     REACT ADMIN UI                              │
│  Dashboard │ Ingestion │ Query │ Agents │ RFP │ Audit │ Metrics │
└────────────────────────┬────────────────────────────────────────┘
                         │ REST + SSE
┌────────────────────────▼────────────────────────────────────────┐
│                  ASP.NET Core Minimal API                       │
│  /api/query  /api/admin/ingest  /api/webhooks  /api/audit       │
└──────┬──────────┬──────────────────┬─────────────────┬──────────┘
       │          │                  │                 │
  AgentOrchestrator  IMessageBus    SharePoint       AuditLogger
       │              (Wolverine)   Webhook          (FedRAMP)
       │              │
       │    ┌─────────▼──────────────────────────────┐
       │    │   MQTT Broker (Mosquitto / HiveMQ)     │
       │    │   Topics:                               │
       │    │   govcon/ingest/document               │
       │    │   govcon/ingest/reconcile              │
       │    │   govcon/ingest/chunk-index            │
       │    └─────────┬──────────────────────────────┘
       │              │
       │    ┌─────────▼──────────────────────────────┐
       │    │   Ingestion Orchestrator               │
       │    │   ├── SharePointAdapter (Graph API)    │
       │    │   ├── DatabaseAdapter (ADO.NET)        │
       │    │   ├── ExcelAdapter (ClosedXML)         │
       │    │   └── CustomApiAdapter                 │
       │    │   Pipeline: Fetch → Chunk → Embed → Index
       │    └─────────┬──────────────────────────────┘
       │              │
       ▼              ▼
┌──────────────────────────────────────────────────────────────────┐
│              LiteGraph 5.x (SQLite-backed)                      │
│  ┌─────────────┐  ┌─────────────┐  ┌────────────────────────┐  │
│  │ Document     │  │   Chunk     │  │   Graph Relationships  │  │
│  │ Nodes        │──│   Nodes     │  │   Doc → Chunk edges    │  │
│  │ (labels,     │  │ + Vectors   │  │   Entity relationships │  │
│  │  tags, data) │  │ (Cosine)    │  │   Traversal / Routes   │  │
│  └─────────────┘  └─────────────┘  └────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
       │
┌──────▼────────────────────────────────────────────────────────────┐
│              Multi-Agent System (Microsoft Agent Framework)       │
│  ┌──────────┐                                                     │
│  │  Router  │ ──→  Accounts │ Contracts │ Ops │ Performance       │
│  │  Agent   │ ──→  Proposal │ Competitor │ Performance Monitor    │
│  └──────────┘                                                     │
│  Each agent: IChatClient + AIFunctionFactory tools + RAG context  │
└───────────────────────────────────────────────────────────────────┘
```

---

## Key Packages

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Agents.AI` | 1.0.0-rc.1 | Agent framework (NOT SemanticKernel) |
| `Microsoft.Agents.AI.Abstractions` | 1.0.0-rc.1 | AIAgent, IChatClient, AIFunctionFactory |
| `Microsoft.Extensions.AI` | 9.10.0 | IEmbeddingGenerator, IChatClient |
| `Microsoft.Extensions.AI.OpenAI` | 9.10.0-preview | OpenAI IChatClient bridge |
| `LiteGraph` | 5.0.2 | Graph DB + vector search (Cosine, InnerProduct) |
| `Wolverine` | latest | MQTT transport, message handlers |
| `Wolverine.MQTT` | latest | MQTT broker transport |
| `OpenAI` | 2.2.0 | OpenAI SDK |
| `Serilog` | 4.x | Structured logging + FedRAMP audit |

---

## Agents

### RouterAgent
Auto-routes every query to the correct specialist by analyzing intent:
- `accounts` → billing, invoicing, AR/AP
- `contracts` → FAR/DFARS, CLINs, task orders
- `operations` → staffing, PMO, deliverables
- `performance` → CPARS, past performance reviews
- `proposal` → RFP analysis, SOW generation, volumes
- `competitor` → competitive intelligence, win probability

### PastPerformanceAgent
- CPARS review and narrative generation
- STAR format (Situation, Task, Action, Result)
- Quantified results for proposal use

### ProposalAgent
- RFP analysis → match score + win probability
- Automatic Technical Volume generation
- Management Volume, SOW generation
- SAM.gov opportunity search integration

### CompetitorAgent
- Identifies likely bidders via SAM.gov + USASpending.gov
- Competitor profiling (past wins, pricing strategy, teaming)
- Win probability calculation
- Discriminator recommendations

### PerformanceMonitorAgent
- AI-generated system health reports
- Ingestion pipeline monitoring
- Latency analysis and optimization recommendations

---

## Ingestion Pipeline

```
Source Document
      ↓
ISourceAdapter.FetchAsync()    ← SharePoint | DB | Excel | Custom
      ↓
RawDocument (text + metadata + SHA-256 hash)
      ↓
Dedup check (LiteGraph hash tag lookup)
      ↓
ChunkerFactory.AutoSelect()    ← Fixed | Paragraph | Semantic
      ↓
EmbeddingPipeline (batched, 32/call)
      ↓
LiteGraphVectorStore.UpsertChunkNodeAsync()
      ↓
LiteGraph edge: Document → Chunk
      ↓
AuditLogger.LogAsync(Ingestion.Completed)
```

### SharePoint 100K Ingestion
```
POST /api/admin/ingest/sharepoint/bulk
{
  "siteId": "abc",
  "driveId": "def",
  "domain": "contracts",
  "tenantId": "acme"
}
```
Streams through Graph API with `@odata.nextLink` pagination.
Each document is published to the MQTT `govcon/ingest/document` topic.
Wolverine handlers process concurrently.

### Webhook + Reconciliation Pattern
```
SharePoint Change → POST /api/webhooks/sharepoint
                  ↓
       ProcessWebhookNotificationAsync (delta API)
                  ↓
       MQTT: IngestDocumentCommand (priority 9)

Backup: ReconciliationBackgroundService
  - Runs every 60 min (configurable)
  - Enumerates all sources, re-ingests changed documents
  - Catches any missed webhooks
```

---

## LiteGraph Vector Search

```csharp
// Extended search: domain-filtered semantic search
var results = await store.VectorSearchAsync(
    queryEmbedding: embedding,
    domain: "contracts",
    topK: 8,
    minScore: 0.72f);

// Graph traversal for related context
var graphContext = await store.GraphContextAsync(
    documentNodeId: nodeId,
    depth: 2);
```

Supports:
- `CosineSimilarity` (default for semantic search)
- `EuclideanDistance`
- `InnerProduct`

---

## FedRAMP Audit (AU-2, AU-3, AU-12)

Every action is logged to an immutable, append-only JSONL audit file:

```json
{
  "id": "...",
  "eventType": "Agent.Query",
  "actor": "jsmith@agency.gov",
  "tenantId": "acme",
  "resourceId": "ContractsAgent",
  "resourceType": "Agent",
  "occurredAt": "2025-10-15T14:23:11Z",
  "traceId": "abc-123",
  "sessionId": "sess-456",
  "ipAddress": "10.0.1.42",
  "outcome": "Success",
  "details": {
    "domain": "Contracts",
    "chunkCount": 8,
    "latencyMs": 1240
  }
}
```

---

## Running Locally

```bash
# 1. Start MQTT broker (Mosquitto)
docker run -p 1883:1883 eclipse-mosquitto

# 2. Configure appsettings.json (OpenAI key, SharePoint creds)

# 3. Run the API
cd src/Api
dotnet run

# API: http://localhost:5000
# Swagger: http://localhost:5000/swagger
```

---

## API Reference

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/query` | RAG query (auto-routed) |
| POST | `/api/query/performance-review` | CPARS review by contract # |
| POST | `/api/query/rfp-analyze` | Analyze RFP text |
| POST | `/api/query/rfp/{id}/generate-volume` | Generate proposal volume |
| POST | `/api/query/competitor-analysis` | Competitor intel |
| POST | `/api/admin/ingest/document` | Enqueue document |
| POST | `/api/admin/ingest/reconcile` | Trigger reconciliation |
| POST | `/api/admin/ingest/sharepoint/bulk` | Bulk SharePoint ingest |
| GET  | `/api/admin/ingest/status/{id}` | Check ingest status |
| POST | `/api/webhooks/sharepoint` | SharePoint webhook receiver |
| GET  | `/api/metrics/ingestion` | Ingestion metrics |
| GET  | `/api/metrics/performance-report` | AI performance report |
| GET  | `/api/audit/events` | Query audit log |
| GET  | `/health` | Health check |

---

## Extending with New Data Sources

Implement `ISourceAdapter`:

```csharp
public class MyCustomAdapter : ISourceAdapter
{
    public DocumentSource SourceType => DocumentSource.CustomApi;

    public async Task<RawDocument?> FetchAsync(string sourceRef, ...) { ... }
    public async IAsyncEnumerable<RawDocument> EnumerateAsync(string rootRef, ...) { ... }
}

// Register in DI:
services.AddSingleton<ISourceAdapter, MyCustomAdapter>();
```

The `IngestionOrchestrator` auto-discovers all registered adapters.
