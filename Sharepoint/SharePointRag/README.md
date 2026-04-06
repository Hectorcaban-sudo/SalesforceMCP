# SharePoint RAG System
### Retrieval-Augmented Generation over 100K+ SharePoint files using Microsoft.Agents.AI & Azure

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        SharePointRag Solution                            │
│                                                                          │
│  ┌─────────────────┐   ┌──────────────────┐   ┌──────────────────────┐  │
│  │  SharePointRag  │   │  SharePointRag   │   │   SharePointRag      │  │
│  │     .Core       │◄──│     .Agent       │   │      .Indexer        │  │
│  │                 │   │                  │   │  (Worker Service)    │  │
│  │ • Crawler       │   │ SharePointRag    │   │                      │  │
│  │ • Extractors    │   │ Agent.cs         │   │ Scheduled full/delta │  │
│  │ • Chunker       │   │ (MS Agents SDK)  │   │ indexing pipeline    │  │
│  │ • Embedder      │   └──────────────────┘   └──────────────────────┘  │
│  │ • VectorStore   │            ▲                        │              │
│  │ • RagOrch.      │            │                        │              │
│  │ • IndexPipeline │   ┌──────────────────┐              │              │
│  └─────────────────┘   │  SharePointRag   │◄─────────────┘              │
│           ▲            │      .Api        │                              │
│           └────────────│                  │                              │
│                        │ POST /api/messages  (MS Agents endpoint)        │
│                        │ POST /api/rag/ask   (REST)                      │
│                        │ POST /api/index/full|delta                      │
│                        └──────────────────┘                              │
└──────────────────────────────────────────────────────────────────────────┘

External Dependencies
─────────────────────
SharePoint Online ──► Microsoft Graph API ──► SharePointCrawler
Azure OpenAI       ──► text-embedding-3-large ──► EmbeddingService
                   ──► gpt-4o                 ──► RagOrchestrator
Azure AI Search    ──► HNSW vector index      ──► VectorStore
```

---

## Project Structure

```
SharePointRag/
├── src/
│   ├── SharePointRag.Core/          # All domain logic (no ASP.NET dependency)
│   │   ├── Configuration/           # Strongly-typed options
│   │   ├── Interfaces/              # All service contracts
│   │   ├── Models/                  # Domain models (records)
│   │   ├── Services/
│   │   │   ├── SharePointCrawler.cs # Graph paging + delta queries
│   │   │   ├── TextExtractors.cs    # PDF, DOCX, PPTX, TXT, HTML
│   │   │   ├── TextChunker.cs       # Token-aware overlapping chunker
│   │   │   ├── EmbeddingService.cs  # Azure OpenAI embeddings (batched)
│   │   │   ├── VectorStore.cs       # Azure AI Search hybrid search
│   │   │   ├── IndexingPipeline.cs  # Channel-based parallel pipeline
│   │   │   ├── RagOrchestrator.cs   # Embed → Retrieve → Generate
│   │   │   └── IndexStateStore.cs   # Delta index state persistence
│   │   └── Extensions/
│   │       └── ServiceCollectionExtensions.cs
│   │
│   ├── SharePointRag.Agent/         # Microsoft.Agents.AI bot handler
│   │   └── SharePointRagAgent.cs
│   │
│   ├── SharePointRag.Api/           # ASP.NET Core host
│   │   ├── Controllers/
│   │   │   ├── RagController.cs     # POST /api/rag/ask
│   │   │   └── IndexController.cs  # POST /api/index/full|delta
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   └── SharePointRag.Indexer/       # Worker Service for scheduled indexing
│       ├── IndexerWorker.cs
│       └── Program.cs
│
├── Dockerfile.api
├── Dockerfile.indexer
├── docker-compose.yml
├── .env.example
└── SharePointRag.sln
```

---

## Prerequisites

| Requirement | Notes |
|---|---|
| .NET 9 SDK | `dotnet --version` must be ≥ 9.0 |
| Azure OpenAI | Deployments: `gpt-4o` + `text-embedding-3-large` |
| Azure AI Search | S1 tier or higher recommended for 100K+ files |
| SharePoint Online | App registration with `Sites.Read.All` + `Files.Read.All` |
| Microsoft Entra ID | Two app registrations (Graph + Bot) |

---

## Quick Start

### 1. Clone and configure secrets

```bash
git clone https://github.com/your-org/sharepoint-rag.git
cd sharepoint-rag
cp .env.example .env
# Edit .env with your real values
```

### 2. Add optional NuGet packages for full extraction support

```bash
# Full DOCX extraction
dotnet add src/SharePointRag.Core package DocumentFormat.OpenXml

# Full PDF extraction
dotnet add src/SharePointRag.Core package PdfPig

# Full PPTX extraction
dotnet add src/SharePointRag.Core package DocumentFormat.OpenXml

# (Optional) Tiktoken-accurate chunking
dotnet add src/SharePointRag.Core package SharpToken
```

Then uncomment the real extraction code in `TextExtractors.cs`.

### 3. Provision the Azure AI Search index

```bash
cd src/SharePointRag.Api
dotnet run
# POST http://localhost:5000/api/index/provision
```

### 4. Run a full index

```bash
# Option A: via REST API
curl -X POST http://localhost:5000/api/index/full

# Option B: run the dedicated indexer worker
dotnet run --project src/SharePointRag.Indexer
```

### 5. Ask questions

```bash
curl -X POST http://localhost:5000/api/rag/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "What is the refund policy for enterprise licenses?"}'
```

---

## Microsoft.Agents SDK Setup (Teams Bot)

### Entra ID App Registration

1. Create an app registration in Entra ID
2. Add a **client secret**
3. Add API permissions:  `https://api.botframework.com/  → .default`

### Bot Framework Registration

1. Go to **Azure Portal → Azure Bot**
2. Set messaging endpoint to: `https://your-domain/api/messages`
3. Copy **App ID** and **Secret** to `AgentAuthentication` config section

### Teams Manifest

Add the bot to a Teams app using App Studio or Developer Portal, pointing the bot ID to your Entra app registration.

---

## Configuration Reference

All settings live in `appsettings.json` (override via environment variables using `__` as separator).

| Section | Key | Description |
|---|---|---|
| `SharePoint` | `SiteUrl` | Full SharePoint site URL |
| `SharePoint` | `LibraryName` | Document library name (default: `Documents`) |
| `SharePoint` | `CrawlParallelism` | Parallel download workers (default: 4, indexer: 8) |
| `SharePoint` | `MaxFileSizeMb` | Skip files larger than this (default: 50) |
| `AzureOpenAI` | `EmbeddingDimensions` | Must match your model (3072 for large-3) |
| `AzureSearch` | `TopK` | Chunks retrieved per query (default: 5) |
| `AzureSearch` | `MinScore` | Minimum reranker score to include (0–1) |
| `Chunking` | `MaxTokensPerChunk` | Chunk size in tokens (default: 512) |
| `Chunking` | `OverlapTokens` | Overlap between chunks (default: 64) |
| `Agent` | `SystemPrompt` | LLM system prompt for RAG generation |
| `Indexer` | `DeltaIntervalMinutes` | How often to run delta index (default: 30) |
| `Indexer` | `FullIndexIntervalHours` | How often to run full index (default: 24) |

---

## Scaling for 100K+ Files

### Indexing throughput

| Setting | Development | Production |
|---|---|---|
| `CrawlParallelism` | 4 | 8–16 |
| `MaxTokensPerChunk` | 512 | 512 |
| AOAI embedding TPM | 100K | 1M+ (request increase) |
| AI Search tier | Basic | S2/S3 |

### State store

The default `JsonFileIndexStateStore` works fine for initial development.  
For production with 100K+ files replace it with **Azure Table Storage**:

```csharp
// In ServiceCollectionExtensions.cs, swap:
services.AddSingleton<IIndexStateStore, AzureTableIndexStateStore>();
```

Implement `IIndexStateStore` against `Azure.Data.Tables` — each row is one `IndexingRecord` keyed by `DriveItemId`.

### Managed Identity (recommended for production)

Replace `ClientSecretCredential` with `DefaultAzureCredential` in `ServiceCollectionExtensions.cs`:

```csharp
var credential = new DefaultAzureCredential();
```

Assign the managed identity:
- `Sites.Read.All` on the SharePoint app
- `Cognitive Services OpenAI User` on the AOAI resource
- `Search Index Data Contributor` on the AI Search resource

---

## Extending the Extractors

Add a new `ITextExtractor` implementation and register it in DI:

```csharp
// In your custom extractor
public sealed class ExcelExtractor : ITextExtractor
{
    public bool CanHandle(string mimeType, string fileName)
        => mimeType.Contains("spreadsheet") || fileName.EndsWith(".xlsx");

    public Task<string> ExtractAsync(Stream content, string mimeType,
        string fileName, CancellationToken ct = default)
    {
        // Use ClosedXML or NPOI here
        ...
    }
}

// In ServiceCollectionExtensions.cs
services.AddSingleton<ITextExtractor, ExcelExtractor>();
```

The `CompositeTextExtractor` picks it up automatically via DI enumeration.

---

## API Reference

### `POST /api/rag/ask`
```json
// Request
{ "question": "What is our vacation policy?" }

// Response
{
  "question": "What is our vacation policy?",
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
Creates the Azure AI Search index schema (idempotent).

### `POST /api/index/full`
Triggers a full library crawl and index (async, returns 202).

### `POST /api/index/delta`
Triggers an incremental index of files changed since last full run (async, returns 202).

### `GET /api/index/status`
Returns `{ "indexExists": true }`.

### `POST /api/messages`
Microsoft.Agents SDK endpoint — receives activities from Bot Framework / Teams.

---

## License

MIT — see LICENSE file.
