# SharePoint RAG — GovCon Past Performance Agent
### .NET 10 · Microsoft.Agents.AI · Azure OpenAI · SharpCoreDB.VectorSearch

---

## Overview

This solution combines a general **SharePoint RAG system** with a specialist
**GovCon Past Performance Agent** — a capture and proposal tool that searches
your SharePoint library of past performance documents, extracts structured
contract records, scores them against RFP requirements, and drafts
FAR 15.305-compliant proposal narratives.

```
Two Microsoft.Agents.AI bots, one backend:

  Teams / WebChat
       │
       ├─ /api/messages                 → SharePointRagAgent   (general Q&A)
       └─ /api/pastperformance/messages → PastPerformanceAgent (GovCon specialist)
                                               │
                                    PastPerformanceOrchestrator
                                    ┌──────────┬───────────────┐
                              QueryParser  ContractExtractor  RelevanceScorer
                                    └──────────┴───────────────┘
                                               │
                                        ProposalDrafter
                                    (narratives + volume)
                                               │
                               SharpCoreDB HNSW Vector Store
                               (encrypted, embedded, no infra)
```

---

## Architecture

### Projects

| Project | Purpose |
|---|---|
| `SharePointRag.Core` | Base RAG infrastructure: crawler, extractors, chunker, embedder, SharpCoreDB vector store, indexing pipeline |
| `SharePointRag.Agent` | General SharePoint knowledge bot (Microsoft.Agents.AI) |
| `SharePointRag.PastPerformance` | ★ GovCon specialist layer — models, services, prompts, bot handler |
| `SharePointRag.Api` | ASP.NET Core 10 host — both agents + all REST endpoints |
| `SharePointRag.Indexer` | Scheduled Worker Service for full + delta indexing |

### Past Performance pipeline

```
User message
    │
    ▼
LlmQueryParser          → GPT-4o structured intent extraction
    │                     Output: SemanticQuery, Intent, Filters
    ▼
IEmbeddingService       → Azure OpenAI text-embedding-3-large
    │
    ▼
SharpCoreDbVectorStore  → HNSW KNN search (SharpCoreDB.VectorSearch)
    │                     Returns: top-K document chunks
    ▼
LlmContractExtractor    → GPT-4o extracts ContractRecord[] from chunks
    │                     (grouped by source file, deduped by contract number)
    ▼
RelevanceScorer         → GovCon scoring: recency, value, CPARS,
    │                     NAICS match, agency match, completeness
    ▼
Intent Router ──────────────────────────────────────────────────┐
    │                                                            │
    ├─ GenerateVolumeSection  → ProposalDrafter.DraftVolumeAsync │
    │                           (parallel narratives + exec sum) │
    ├─ FindReferences         → CO/COR contact extraction        │
    ├─ ExtractCPARSRatings    → Ratings markdown table           │
    ├─ FindKeyPersonnel       → Personnel roster                 │
    ├─ SummarisePortfolio     → GPT-4o portfolio summary         │
    ├─ IdentifyGaps           → GPT-4o gap analysis + risk       │
    └─ FindSimilarContracts   → Ranked list + GPT-4o answer ─────┘
    │
    ▼
PastPerformanceResponse  (Answer, RelevantContracts, DraftedSection, Citations, Warnings)
```

---

## GovCon Capabilities

### 1. Smart Intent Detection
The agent automatically classifies your question into one of 7 intents — no commands needed:

| Intent | Trigger phrases | Output |
|---|---|---|
| **FindSimilarContracts** | "similar to", "relevant to this SOW", "like this RFP" | Ranked contract list with relevance rationale |
| **GenerateVolumeSection** | "draft", "write", "volume for solicitation" | Full narratives + executive summary + gap flags |
| **FindReferences** | "CO reference", "who is our contact", "contracting officer" | Formatted CO/COR contact blocks |
| **SummarisePortfolio** | "summarise", "overview", "what's our experience in" | Portfolio executive summary |
| **IdentifyGaps** | "gaps", "do we have", "NAICS", "missing" | Gap analysis with risk level + mitigation |
| **ExtractCPARSRatings** | "CPARS", "ratings", "performance scores" | Markdown table of all ratings |
| **FindKeyPersonnel** | "who has led", "key personnel", "experienced staff" | Personnel roster with relevant contracts |

### 2. Structured Contract Extraction (FAR-aligned)
Every document chunk is parsed into a strongly-typed `ContractRecord` containing:
- Contract number, type (FFP/CPFF/T&M/IDIQ), agency, period of performance, value
- CPARS ratings (Overall, Quality, Schedule, Cost Control, Management, Small Business)
- Contracting Officer + COR name, phone, email
- NAICS and PSC codes
- Key accomplishments (measurable outcomes)
- Key personnel with clearance levels
- Subcontractors and teaming roles

### 3. GovCon Relevance Scoring
Contracts are scored against each query using weighted GovCon criteria:

| Factor | Weight | Logic |
|---|---|---|
| Recency | 30% | Decays linearly over 10 years; ongoing = max score |
| Dollar value | 20% | Peaks when value ≥ MinValueFilter; log-scale otherwise |
| CPARS ratings | 25% | Exceptional=1.0, Very Good=0.8, Satisfactory=0.6… |
| NAICS match | 15% | Bonus for exact 6-digit code match |
| Agency match | 10% | Bonus when agency name/acronym matches filter |
| Completeness | bonus | +0.05 each for CO email, accomplishments, CPARS present |

Contracts older than `RecencyYearsFilter` are penalised 90% but not excluded.

### 4. Proposal-Ready Narrative Drafting
Each narrative follows FAR 15.305(a)(2) requirements:
- Opens with relevance statement linking scope to solicitation
- States contract number, agency, period, dollar value
- Includes CPARS ratings
- ≥3 specific, measurable accomplishments
- CO/COR reference block
- ~500 words, active voice, third person
- Flags missing data with `[VERIFY]`

### 5. Automated Gap Detection
After drafting, the agent flags:
- Missing CO/COR contact info (required by most RFPs)
- Missing CPARS ratings (links to CPARS.gov)
- Contracts older than 6 years (may fail recency requirements)
- Contracts without measurable accomplishments

---

## Project Structure

```
SharePointRag/
├── src/
│   ├── SharePointRag.Core/
│   │   ├── Configuration/Options.cs              # SharpCoreDbOptions + all options
│   │   ├── Interfaces/Interfaces.cs              # IVectorStore, IRagOrchestrator, …
│   │   ├── Models/Models.cs                      # DocumentChunk, RetrievedChunk, …
│   │   └── Services/
│   │       ├── SharePointCrawler.cs              # MS Graph paging + delta
│   │       ├── TextExtractors.cs                 # PDF, DOCX, TXT, HTML
│   │       ├── TextChunker.cs                    # Token-aware chunker
│   │       ├── EmbeddingService.cs               # Azure OpenAI batched embeddings
│   │       ├── VectorStore.cs                    # SharpCoreDbVectorStore (HNSW)
│   │       ├── IndexingPipeline.cs               # Parallel channel pipeline
│   │       ├── RagOrchestrator.cs                # General RAG pipeline
│   │       └── IndexStateStore.cs                # Delta state
│   │
│   ├── SharePointRag.Agent/
│   │   └── SharePointRagAgent.cs                 # General bot (MS Agents SDK)
│   │
│   ├── SharePointRag.PastPerformance/            # ★ GovCon specialist layer
│   │   ├── Models/PastPerformanceModels.cs       # ContractRecord, QueryIntent, …
│   │   ├── Interfaces/IPastPerformanceInterfaces.cs
│   │   ├── Prompts/PastPerformancePrompts.cs     # All LLM prompt templates
│   │   ├── Services/
│   │   │   ├── QueryParser.cs                    # GPT-4o intent extraction
│   │   │   ├── ContractExtractor.cs              # GPT-4o structured extraction
│   │   │   ├── RelevanceScorer.cs                # GovCon scoring algorithm
│   │   │   ├── ProposalDrafter.cs                # Narrative + volume drafting
│   │   │   └── PastPerformanceOrchestrator.cs    # Top-level intent router
│   │   ├── Extensions/PastPerformanceServiceExtensions.cs
│   │   └── PastPerformanceAgent.cs               # MS Agents SDK bot handler
│   │
│   ├── SharePointRag.Api/
│   │   ├── Controllers/
│   │   │   ├── RagController.cs                  # POST /api/rag/ask
│   │   │   ├── IndexController.cs                # POST /api/index/*
│   │   │   └── PastPerformanceController.cs      # POST /api/pastperformance/*
│   │   ├── Program.cs                            # Both agents wired
│   │   └── appsettings.json
│   │
│   └── SharePointRag.Indexer/
│       ├── IndexerWorker.cs
│       └── Program.cs
│
├── teams-manifests/
│   ├── general-rag/manifest.json                 # Teams app for SharePoint RAG bot
│   └── past-performance/manifest.json            # Teams app for PP Agent bot
│
├── Dockerfile.api
├── Dockerfile.indexer
├── docker-compose.yml
├── .env.example
└── SharePointRag.sln
```

---

## Quick Start

### 1. Configure secrets

```bash
cp .env.example .env
# Fill in all values in .env
```

### 2. Add extraction packages (optional but recommended)

```bash
dotnet add src/SharePointRag.Core package PdfPig
dotnet add src/SharePointRag.Core package DocumentFormat.OpenXml
```
Then uncomment the extraction code in `TextExtractors.cs`.

### 3. Provision and index

```bash
# Start the API
dotnet run --project src/SharePointRag.Api

# Provision the SharpCoreDB schema + HNSW index
curl -X POST http://localhost:5000/api/index/provision

# Run full index of your SharePoint library
curl -X POST http://localhost:5000/api/index/full
# (Monitor logs — for 100K files this takes time)
```

### 4. Test the Past Performance Agent

```bash
# Find relevant contracts
curl -X POST http://localhost:5000/api/pastperformance/ask \
  -H "Content-Type: application/json" \
  -d '{"question": "Find IT modernisation contracts similar to a FISMA compliance SOW"}'

# Draft a full Past Performance Volume
curl -X POST http://localhost:5000/api/pastperformance/volume \
  -H "Content-Type: application/json" \
  -d '{"solicitationContext": "HHS-2024-IT-001: Federal health IT system modernisation, NAICS 541512, $50M ceiling"}'

# Download volume as plain text
curl -X POST http://localhost:5000/api/pastperformance/volume/download \
  -H "Content-Type: application/json" \
  -d '{"solicitationContext": "..."}' \
  --output PastPerformanceVolume.txt

# Check CPARS ratings for DoD work
curl "http://localhost:5000/api/pastperformance/cpars?agency=DoD"

# Identify gaps
curl -X POST http://localhost:5000/api/pastperformance/gaps \
  -H "Content-Type: application/json" \
  -d '{"requirements": "NAICS 541512, min $10M single award, within 5 years, federal civilian agency"}'
```

### 5. Deploy to Teams

1. Update `teams-manifests/past-performance/manifest.json` with your bot app ID.
2. Zip the manifest folder: `color.png`, `outline.png`, `manifest.json`.
3. Upload to Teams Admin Center or sideload in Teams Developer Portal.
4. The bot messaging endpoint: `https://your-domain/api/pastperformance/messages`

### 6. Docker Compose (full stack)

```bash
docker compose up --build
```

---

## Documents to Index for Best Results

Load the following into your SharePoint library:

| Document Type | Why It Matters |
|---|---|
| Past Performance Questionnaires (PPQs) | Contains structured contract data, ratings, CO contacts |
| CPARS printouts from CPARS.gov | Official performance ratings |
| Contract award documents (SF-26, SF-1449) | Contract numbers, values, period of performance |
| Performance Work Statements (PWS/SOW) | Scope context for relevance matching |
| Lessons Learned reports | Key accomplishments and challenge/resolution pairs |
| Prior proposal Past Performance volumes | Pre-formatted narratives to reuse and update |
| Subcontract agreements | Teaming partner and subcontractor roles |
| Key Personnel resumes / qualifications | Personnel matching for `/FindKeyPersonnel` intent |

---

## API Reference — Past Performance

### `POST /api/pastperformance/ask`
General question — agent detects intent automatically.
```json
// Request
{ "question": "What CPARS ratings do we have for HHS contracts in the last 3 years?" }

// Response: PastPerformanceResponse
{
  "query":   { "intent": "ExtractCPARSRatings", "agencyFilter": "HHS", "recencyYearsFilter": 3 },
  "answer":  "| Contract | Agency | Overall | ... |",
  "relevantContracts": [...],
  "citations": [...],
  "warnings": [...]
}
```

### `POST /api/pastperformance/volume`
Draft a complete Past Performance Volume.
```json
// Request
{
  "solicitationContext": "W912DQ-24-R-0041 — Army logistics IT modernisation, NAICS 541512, CPFF, $25M",
  "maxContracts": 5
}
// Response: PastPerformanceVolumeSection (narratives, executiveSummary, flaggedGaps)
```

### `POST /api/pastperformance/volume/download`
Same as `/volume` but returns a plain-text `.txt` file download.

### `POST /api/pastperformance/contracts/search`
Structured contract search.
```json
{ "sowDescription": "Agile software development for federal health IT", "naicsCode": "541512", "topK": 5 }
```

### `GET /api/pastperformance/cpars?agency=DoD`
CPARS ratings table filtered by agency (optional).

### `POST /api/pastperformance/gaps`
Gap analysis against solicitation requirements.
```json
{ "requirements": "Min $10M, NAICS 541512, within 5 years, civilian agency" }
```

### `POST /api/pastperformance/messages`
Microsoft.Agents SDK endpoint for the Teams Past Performance bot.

---

## Configuration Reference

| Section | Key | Default | Description |
|---|---|---|---|
| `SharpCoreDB` | `TopK` | `8` | KNN neighbours (PP agent uses more than general RAG) |
| `SharpCoreDB` | `MinScore` | `0.45` | Slightly lower for PP to capture broader matches |
| `AzureOpenAI` | `ChatDeployment` | `gpt-4o` | Used by all LLM calls; gpt-4o recommended for extraction accuracy |
| `AzureOpenAI` | `EmbeddingDeployment` | `text-embedding-3-large` | 3072-dim for maximum semantic accuracy |

---

## License

MIT
