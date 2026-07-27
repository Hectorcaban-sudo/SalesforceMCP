# SF-1449 Contract Manager

A C# solution that uses **Microsoft Agent Framework (`Microsoft.Agents.AI`)** to read a
fully-executed SF-1449 ("Solicitation/Contract/Order for Commercial Products and
Commercial Services") PDF, extract every header field / CLIN / FAR-DFARS clause, and
persist it to a database — with a review UI that highlights exactly what the AI found
and how confident it was (styled after the Salesforce contract-viewer screenshot you
shared), plus a full manual data-entry/CRUD screen.

## Projects

```
SF1449ContractManager.sln
├── src/SF1449ContractManager.Core   <-- models, EF Core, extraction agent (no UI)
└── src/SF1449ContractManager.Web    <-- Blazor Server app (review UI + CRUD forms)
```

### Core

| File | Purpose |
|---|---|
| `Models/Sf1449Contract.cs` | Header entity — every property maps to a specific SF-1449 block number (see comments). |
| `Models/ContractLineItem.cs` | Blocks 19–24, one row per CLIN. |
| `Models/ContractClause.cs` | One row per FAR/DFARS/agency clause, tagged with which section of the package it came from (Contract Clauses, Addendum to Contract Clauses, Solicitation Provisions, Addendum to Solicitation Provisions, Offeror Reps & Certs). |
| `Models/FieldExtraction.cs` | Confidence + source-page metadata per header field — this is what drives the highlight colors in the review UI. |
| `Data/ContractDbContext.cs` | EF Core context (SQLite by default; swap the provider for SQL Server/Postgres in production). |
| `Extraction/PdfTextExtractor.cs` | Pulls raw text per page out of the PDF with PdfPig (pure managed, no native deps). If your PDFs are scanned images with no text layer, run OCR first (e.g. Azure AI Document Intelligence) and feed the resulting text in the same way. |
| `Extraction/ExtractionPrompts.cs` | The field catalogue + system prompt given to the agent — this is the single source of truth for "what fields do we extract"; add a row here and a matching property on `Sf1449Contract` to extract a new field. |
| `Extraction/Sf1449ExtractionAgent.cs` | The `Microsoft.Agents.AI` agent itself: wraps an `IChatClient`, asks for strict JSON, and maps the response onto the EF entity graph via reflection. |
| `Repositories/ContractRepository.cs` | CRUD used by both the review screen and the data-entry screen. |

### Web (Blazor Server)

| Page | Route | Purpose |
|---|---|---|
| `Home.razor` | `/` | Contract list + PDF upload/scan. |
| `ContractReview.razor` | `/contracts/{id}/review` | **The split-screen scan-review UI** — PDF on the left, extracted fields grouped by SF-1449 block on the right, each field highlighted green/yellow/red by confidence, with a "jump to source page" link and inline correction before you approve. |
| `ContractEdit.razor` | `/contracts/new`, `/contracts/{id}/edit` | Full manual data-entry form: every header field, plus add/remove rows for line items and clauses. Works for both creating a new record from scratch and editing an AI-extracted one. |

## How extraction works end to end

1. User uploads a PDF on `/`.
2. `PdfTextExtractor` pulls text per page and tags it `[[PAGE n]]`.
3. `Sf1449ExtractionAgent` (a `Microsoft.Agents.AI.ChatClientAgent` under the hood, created
   via `chatClient.CreateAIAgent(...)`) is given the tagged text and a strict-JSON
   instruction set built from the field catalogue in `ExtractionPrompts.cs`.
4. The JSON response is deserialized and mapped by reflection onto `Sf1449Contract`,
   `ContractLineItem`, and `ContractClause`, while every header field also gets a
   `FieldExtraction` row recording confidence + source page.
5. The contract is saved with `Status = PendingReview` and the user lands on the
   review screen to confirm/correct fields before approving.

## Running it

1. **Configure an AI provider** — don't put real secrets in `appsettings.json`; use
   user-secrets instead:
   ```bash
   cd src/SF1449ContractManager.Web
   dotnet user-secrets init
   dotnet user-secrets set "AIProvider:OpenAI:ApiKey" "sk-..."
   # or, for Azure OpenAI:
   dotnet user-secrets set "AIProvider:Provider" "AzureOpenAI"
   dotnet user-secrets set "AIProvider:AzureOpenAI:Endpoint" "https://<resource>.openai.azure.com/"
   dotnet user-secrets set "AIProvider:AzureOpenAI:DeploymentName" "gpt-4o"
   dotnet user-secrets set "AIProvider:AzureOpenAI:ApiKey" "..."
   ```
2. **Restore & run**:
   ```bash
   dotnet restore
   dotnet run --project src/SF1449ContractManager.Web
   ```
   The SQLite database is created automatically on first run (`App_Data/contracts.db`,
   via `EnsureCreated()`). Swap in EF Core migrations before you go to production so
   schema changes are tracked properly.
3. Browse to the app, upload `W912WJ25QA026.pdf`, and you'll land on the review screen
   once extraction finishes.

## Known limitations / next steps to harden this for production

- **NuGet package versions** in the `.csproj` files (`Microsoft.Agents.AI`, `Microsoft.Extensions.AI.OpenAI`, etc.) were current as of mid-2026 but the framework is in active preview — pin/update versions with `dotnet outdated` before shipping, and check the [Microsoft Agent Framework docs](https://learn.microsoft.com/en-us/agent-framework/) for the current `AIAgent` surface.
- **Very large PDFs**: this sends the whole document text in a single agent call. For 100+ page solicitations, chunk by section (Header, PWS, Clauses) and issue one agent call per chunk, or use a model with a large context window.
- **Scanned (non-text) PDFs** need an OCR pass before `PdfTextExtractor` — it does not do OCR itself.
- **`ContractRepository.UpdateAsync`** replaces line items/clauses wholesale on every save, which is simple and correct for a form-based UI but not efficient for very large clause lists updated frequently — switch to a diff-based update if that becomes a bottleneck.
- **Authentication/authorization** is not included — add ASP.NET Core Identity or your org's SSO before exposing this beyond a trusted internal network, since it handles procurement-sensitive data.
- **52.212-3 offeror representations & certifications** (the giant checkbox form) is modeled generically via `ContractClause` rows with `Section = OfferorRepresentationsAndCertifications` rather than one column per checkbox — add a dedicated `OfferorCertification` entity if you need to query/report on individual reps.
