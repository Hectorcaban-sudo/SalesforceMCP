# SharePoint RAG System
### Retrieval-Augmented Generation over 100K+ SharePoint files
### Stack: Microsoft.Agents.AI · Azure OpenAI · SharpCoreDB + SharpCoreDB.VectorSearch

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        SharePointRag Solution (.NET 10)                  │
│                                                                          │
│  ┌─────────────────┐   ┌──────────────────┐   ┌──────────────────────┐  │
│  │  SharePointRag  │   │  SharePointRag   │   │   SharePointRag      │  │
│  │     .Core       │◄──│     .Agent       │   │      .Indexer        │  │
│  │                 │   │ (MS Agents SDK)  │   │  (Worker Service)    │  │
│  │ • Crawler       │   └──────────────────┘   └──────────────────────┘  │
│  │ • Extractors    │            ▲                        │               │
│  │ • Chunker       │            │                        │               │
│  │ • Embedder      │   ┌──────────────────┐              │               │
│  │ • VectorStore   │◄──│  SharePointRag   │◄─────────────┘               │
│  │ • RagOrch.      │   │      .Api        │                              │
│  │ • IndexPipeline │   │                  │                              │
│  └─────────────────┘   │ /api/messages    │  ← MS Agents bot endpoint   │
│           │            │ /api/rag/ask     │  ← REST RAG endpoint        │
│           │            │ /api/index/*     │  ← Indexing control         │
│           ▼            └──────────────────┘                              │
│  ┌──────────────────────────────────────────────────────────────────┐    │
│  │              SharpCoreDB (encrypted file-based DB)               │    │
│  │  SQL table: chunks (metadata)                                    │    │
│  │  VectorSearch: GraphRagEngine HNSW index (float32, cosine)       │    │
│  │  File: /app/data/scdb  ← AES-256-GCM encrypted, shared volume   │    │
│  └──────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────┘

External Services
─────────────────
SharePoint Online  →  Microsoft Graph API    →  SharePointCrawler
Azure OpenAI       →  text-embedding-3-large →  EmbeddingService (batched)
Azure OpenAI       →  gpt-4o                →  RagOrchestrator
```

---

## Vector Store: SharpCoreDB + SharpCoreDB.VectorSearch

### Why SharpCoreDB?

| Property | SharpCoreDB |
|---|---|
| Deployment | **Zero infrastructure** – embedded file-based DB, no server process |
| Encryption | **AES-256-GCM** on all data at rest |
| Vector index | **HNSW + SIMD** – 50-100× faster than SQLite, sub-ms at millions of vectors |
| Platform | .NET 10, NativeAOT compatible, cross-platform |
| Persistence | Single shared encrypted directory mounted as a Docker volume |

### Storage Layout

```
/app/data/scdb/
├── chunks.scdb          ← SQL table: chunk metadata (id, fileName, webUrl, content, …)
├── chunk_embeddings/    ← GraphRagEngine HNSW index (float32 vectors, cosine distance)
└── index-state.json     ← Delta tracking (last-indexed timestamps per driveItemId)
```

### Key API Usage

```csharp
// DI registration (ServiceCollectionExtensions.cs)
services.AddSharpCoreDB()
        .AddVectorSupport();

// Initialise engine (SharpCoreDbVectorStore.cs)
var engine = new GraphRagEngine(db, chunksTable, embeddingsCollection, dims);
await engine.InitializeAsync();

// Index a batch of chunks
await engine.IndexEmbeddingsAsync(
    chunks.Select(c => new NodeEmbedding(c.Id, c.Embedding)).ToList());

// KNN search
var results = await engine.SearchAsync(queryVector, topK: 5);
// result.NodeId  → chunk id → resolve metadata from SQL
// result.Score   → cosine similarity [0,1]
```

### Packages

```xml
<PackageReference Include="SharpCoreDB"              Version="1.7.0" />
<PackageReference Include="SharpCoreDB.VectorSearch" Version="1.7.0" />
```

---

## Project Structure

```
SharePointRag/
├── src/
│   ├── SharePointRag.Core/                      # All domain logic
│   │   ├── Configuration/Options.cs             # SharpCoreDbOptions + all options
│   │   ├── Interfaces/Interfaces.cs             # IVectorStore, IRagOrchestrator, …
│   │   ├── Models/Models.cs                     # DocumentChunk, RetrievedChunk, …
│   │   └── Services/
│   │       ├── SharePointCrawler.cs             # MS Graph paging + delta queries
│   │       ├── TextExtractors.cs                # PDF, DOCX, TXT, HTML (pluggable)
│   │       ├── TextChunker.cs                   # Token-aware overlapping chunker
│   │       ├── EmbeddingService.cs              # Azure OpenAI batched embeddings
│   │       ├── VectorStore.cs                   # ★ SharpCoreDbVectorStore (HNSW)
│   │       ├── IndexingPipeline.cs              # Channel-based parallel pipeline
│   │       ├── RagOrchestrator.cs               # Embed → HNSW KNN → GPT-4o
│   │       └── IndexStateStore.cs               # Delta state persistence
│   │
│   ├── SharePointRag.Agent/
│   │   └── SharePointRagAgent.cs               # Microsoft.Agents.AI handler
│   │
│   ├── SharePointRag.Api/
│   │   ├── Controllers/
│   │   │   ├── RagController.cs                # POST /api/rag/ask
│   │   │   └── IndexController.cs             # POST /api/index/{provision,full,delta}
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   └── SharePointRag.Indexer/
│       ├── IndexerWorker.cs                    # Scheduled full + delta indexing
│       └── Program.cs
│
├── Dockerfile.api
├── Dockerfile.indexer
├── docker-compose.yml                         # No Redis/external DB needed
├── .env.example
└── SharePointRag.sln
```

---

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET 10 SDK | SharpCoreDB v1.7.0 targets net10.0 |
| Docker | For containerised deployment |
| Azure OpenAI | Deployments: `gpt-4o` + `text-embedding-3-large` |
| SharePoint Online | App registration: `Sites.Read.All`, `Files.Read.All` |
| Microsoft Entra ID | Two app registrations (Graph crawler + Bot Framework) |

> **No Redis, no Azure AI Search, no external vector DB** — SharpCoreDB runs entirely embedded in the process and persists to an encrypted local directory.

---

## Quick Start

### 1. Clone and configure

```bash
git clone https://github.com/your-org/sharepoint-rag.git
cd sharepoint-rag
cp .env.example .env
# Fill in .env with your real values
```

### 2. Add optional text-extraction packages

```bash
# Full PDF extraction (uncomment PdfPig code in TextExtractors.cs)
dotnet add src/SharePointRag.Core package PdfPig

# Full DOCX / PPTX extraction (uncomment OpenXml code in TextExtractors.cs)
dotnet add src/SharePointRag.Core package DocumentFormat.OpenXml
```

### 3. Provision the SharpCoreDB schema

```bash
dotnet run --project src/SharePointRag.Api
curl -X POST http://localhost:5000/api/index/provision
# → Creates encrypted .scdb files + HNSW index structure
```

### 4. Run a full index

```bash
# Option A: trigger via REST (fire-and-forget, watch logs)
curl -X POST http://localhost:5000/api/index/full

# Option B: run the dedicated worker
dotnet run --project src/SharePointRag.Indexer
```

### 5. Ask a question

```bash
curl -X POST http://localhost:5000/api/rag/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "What is the company refund policy?"}'
```

### 6. Docker Compose (full stack)

```bash
docker compose up --build
# API:     http://localhost:5000
# Swagger: http://localhost:5000/docs
```

---

## Configuration Reference

### `SharpCoreDB` section

| Key | Default | Description |
|---|---|---|
| `DataDirectory` | `/app/data/scdb` | Directory for encrypted .scdb files |
| `EncryptionPassword` | *(required)* | AES-256-GCM password – use env var or secrets |
| `ChunksTable` | `chunks` | SQL table name for chunk metadata |
| `EmbeddingsCollection` | `chunk_embeddings` | GraphRagEngine HNSW collection name |
| `TopK` | `5` | KNN neighbours to retrieve per query |
| `MinScore` | `0.5` | Minimum cosine similarity threshold (0–1) |

### Setting the encryption password securely

```bash
# .NET user-secrets (local dev)
dotnet user-secrets set "SharpCoreDB:EncryptionPassword" "my-strong-password" \
  --project src/SharePointRag.Api

# Environment variable (Docker / Azure)
SharpCoreDB__EncryptionPassword=my-strong-password
```

### Full `SharePoint` section

| Key | Default | Description |
|---|---|---|
| `SiteUrl` | – | Full SharePoint site URL |
| `LibraryName` | `Documents` | Document library name |
| `CrawlParallelism` | `4` (API) / `8` (Indexer) | Parallel download workers |
| `MaxFileSizeMb` | `50` | Skip files larger than this |
| `AllowedExtensions` | `.pdf .docx .pptx …` | Extensions to index |

---

## Scaling for 100K+ Files

| Concern | Recommendation |
|---|---|
| Initial index speed | Set `CrawlParallelism: 8–16` in the Indexer config |
| AOAI embedding quota | Request TPM limit increase for `text-embedding-3-large` |
| SharpCoreDB memory | HNSW for 3072-dim float32: ~12 KB/vector → 1M chunks ≈ 12 GB |
| Persistence durability | Mount `rag_data` volume to Azure Files or equivalent |
| State store at scale | Swap `JsonFileIndexStateStore` for a SharpCoreDB SQL table impl |

### Multi-process write safety

SharpCoreDB uses file-level locking. For the API + Indexer running concurrently
in Docker Compose, writes from the Indexer and reads from the API are
serialised automatically by the database engine — no additional coordination
needed.

---

## Extending Text Extractors

Drop in a new `ITextExtractor` and register it — no other changes needed:

```csharp
public sealed class ExcelExtractor : ITextExtractor
{
    public bool CanHandle(string mimeType, string fileName)
        => fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(Stream content, string mimeType,
        string fileName, CancellationToken ct = default)
    {
        // Use ClosedXML, NPOI, or MiniExcel here
        ...
    }
}

// In ServiceCollectionExtensions.cs
services.AddSingleton<ITextExtractor, ExcelExtractor>();
```

`CompositeTextExtractor` picks it up via DI enumeration automatically.

---

## API Reference

### `POST /api/rag/ask`
```json
// Request
{ "question": "What is the vacation policy?" }

// Response
{
  "question": "What is the vacation policy?",
  "answer": "Employees accrue 15 days PTO per year...",
  "sources": [
    {
      "fileName": "HR-Policy-2024.docx",
      "webUrl": "https://contoso.sharepoint.com/...",
      "chunkIndex": 3,
      "totalChunks": 12,
      "score": 0.9231
    }
  ]
}
```

### `POST /api/index/provision`
Creates the SharpCoreDB schema + HNSW index structure. Idempotent.

### `GET  /api/index/status`
Returns `{ "indexExists": true }`.

### `POST /api/index/full`
Full library crawl → extract → chunk → embed → HNSW upsert. Returns 202.

### `POST /api/index/delta`
Incremental index of files changed since last full run. Returns 202.

### `POST /api/messages`
Microsoft.Agents SDK endpoint for Teams / Bot Framework activities.

---

## License

MIT
