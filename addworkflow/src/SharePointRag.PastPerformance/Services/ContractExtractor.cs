using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Models;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Prompts;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Source-aware contract extractor.
///
/// Routes each retrieved chunk to one of two extraction strategies based on the
/// connector type that produced it:
///
/// ── Document sources (SharePoint, Custom) ────────────────────────────────────
///   Groups chunks by document title, concatenates content, sends to GPT-4o
///   for full unstructured extraction. Handles PPQs, CPARS printouts, proposal
///   volumes — any free-text past performance document.
///
/// ── Structured sources (SqlDatabase, Deltek, Excel) ──────────────────────────
///   Each chunk carries rich Metadata from the connector (SQL columns, Deltek API
///   fields, Excel header values). TryDirectMapping() reads field names from the
///   declared MetadataSchema first, then falls back to well-known aliases. If
///   anchor data is still insufficient, falls back to LLM enrichment.
///
/// MetadataSchema integration:
///   When a DataSourceDefinition declares a MetadataSchema, TryDirectMapping reads
///   the schema's field names directly — so custom column names (e.g. "PROJ_ID",
///   "CO_EMAIL", "CPARS_OVERALL") map to ContractRecord fields without any code
///   changes. This is especially useful for ReadOnly sources where you define the
///   schema to describe data indexed by an external system.
///
/// Deduplication:
///   Records with the same ContractNumber are collapsed; structured source records
///   are preferred over document-extracted ones when values conflict.
/// </summary>
public sealed class LlmContractExtractor(
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    IOptions<RagRegistryOptions> registryOpts,
    ILogger<LlmContractExtractor> logger) : IContractExtractor
{
    private readonly AzureOpenAIOptions  _aoai     = aoaiOpts.Value;
    private readonly RagRegistryOptions  _registry = registryOpts.Value;

    private static readonly HashSet<string> StructuredTypes =
        new(StringComparer.OrdinalIgnoreCase)
        { "SqlDatabase", "Deltek", "Excel" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task<List<ContractRecord>> ExtractAsync(
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken ct = default)
    {
        if (chunks.Count == 0) return [];

        var documentChunks   = new List<RetrievedChunk>();
        var structuredChunks = new List<RetrievedChunk>();

        foreach (var rc in chunks)
        {
            var connectorType = rc.Chunk.Metadata.TryGetValue("ConnectorType", out var ct2)
                ? ct2 : rc.Chunk.DataSourceName;

            if (StructuredTypes.Contains(connectorType))
                structuredChunks.Add(rc);
            else
                documentChunks.Add(rc);
        }

        var allRecords = new List<ContractRecord>();

        if (documentChunks.Count > 0)
            allRecords.AddRange(await ExtractFromDocumentsAsync(documentChunks, ct));

        if (structuredChunks.Count > 0)
            allRecords.AddRange(await EnrichFromStructuredSourcesAsync(structuredChunks, ct));

        // Deduplicate by ContractNumber — prefer structured (authoritative field data)
        return allRecords
            .GroupBy(r => NormaliseContractNumber(r.ContractNumber))
            .Select(g =>
                g.FirstOrDefault(r => StructuredTypes.Contains(r.ConnectorType))
                ?? g.OrderByDescending(r => r.RelevanceScore).First())
            .ToList();
    }

    // ── Document extraction (SharePoint, Custom) ──────────────────────────────

    private async Task<List<ContractRecord>> ExtractFromDocumentsAsync(
        List<RetrievedChunk> chunks, CancellationToken ct)
    {
        var byDocument = chunks
            .GroupBy(c => $"{c.Chunk.DataSourceName}::{c.Chunk.Title}")
            .ToList();

        var results = new List<ContractRecord>();
        foreach (var group in byDocument)
        {
            var title          = group.First().Chunk.Title;
            var url            = group.First().Chunk.Url;
            var dataSourceName = group.First().Chunk.DataSourceName;
            var connectorType  = group.First().Chunk.Metadata.TryGetValue("ConnectorType", out var ct2)
                ? ct2 : "SharePoint";

            var combinedText = new StringBuilder();
            foreach (var rc in group.OrderBy(c => c.Chunk.ChunkIndex))
                combinedText.AppendLine(rc.Chunk.Content).AppendLine();

            logger.LogDebug("[PP] Document extraction: {Title} ({N} chunks) from {Src}",
                title, group.Count(), dataSourceName);

            results.AddRange(await CallExtractionLlmAsync(
                combinedText.ToString(), title, url, dataSourceName, connectorType, ct));
        }
        return results;
    }

    private async Task<List<ContractRecord>> CallExtractionLlmAsync(
        string text, string sourceTitle, string url,
        string dataSourceName, string connectorType, CancellationToken ct)
    {
        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var userContent = PastPerformancePrompts.ContractExtractionUserTemplate
            .Replace("{sourceFile}",    sourceTitle)
            .Replace("{connectorType}", connectorType)
            .Replace("{content}",       text.Length > 12_000 ? text[..12_000] : text);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.ContractExtractionSystem),
            new UserChatMessage(userContent)
        };

        var response = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 2048, Temperature = 0.0f }, ct);

        var json = CleanJson(response.Value.Content[0].Text);
        try
        {
            var records = JsonSerializer.Deserialize<List<ContractRecord>>(json, JsonOpts) ?? [];
            return records.Select(r => r with
            {
                Id                = Guid.NewGuid().ToString("N"),
                DataSourceName    = dataSourceName,
                ConnectorType     = connectorType,
                SourceDocumentUrl = url,
                SourceFileName    = sourceTitle
            }).ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[PP] Extraction JSON parse failed for {T}. JSON: {J}",
                sourceTitle, json.Length > 400 ? json[..400] : json);
            return [];
        }
    }

    // ── Structured enrichment (SqlDatabase, Deltek, Excel) ────────────────────

    private async Task<List<ContractRecord>> EnrichFromStructuredSourcesAsync(
        List<RetrievedChunk> chunks, CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(4);
        var tasks = chunks.Select(rc => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct);
            try   { return await EnrichSingleRecordAsync(rc, ct); }
            finally { semaphore.Release(); }
        }, ct));

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    private async Task<ContractRecord?> EnrichSingleRecordAsync(
        RetrievedChunk rc, CancellationToken ct)
    {
        var chunk          = rc.Chunk;
        var connectorType  = chunk.Metadata.TryGetValue("ConnectorType", out var ct2) ? ct2 : "Structured";
        var dataSourceName = chunk.DataSourceName;

        // Resolve the declared MetadataSchema for this data source (may be empty)
        var schema = GetSchema(dataSourceName);

        var direct = TryDirectMapping(chunk, connectorType, dataSourceName, schema);
        if (direct is not null)
        {
            logger.LogDebug("[PP] Direct-mapped '{Title}' from {Src}", chunk.Title, dataSourceName);
            return direct;
        }

        // LLM enrichment fallback — includes schema hint if available
        logger.LogDebug("[PP] LLM enrichment for '{Title}' from {Src}", chunk.Title, dataSourceName);

        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var metadataText = string.Join("\n",
            chunk.Metadata.Select(kv => $"  {kv.Key}: {kv.Value}"));

        // Append schema description so the LLM knows what the fields mean
        var schemaHint = BuildSchemaHint(schema);

        var systemPrompt = PastPerformancePrompts.StructuredEnrichmentSystem
            .Replace("{connectorType}", connectorType);

        var userContent = PastPerformancePrompts.StructuredEnrichmentUserTemplate
            .Replace("{connectorType}", connectorType)
            .Replace("{sourceName}",   dataSourceName)
            .Replace("{title}",        chunk.Title)
            .Replace("{url}",          chunk.Url)
            .Replace("{metadata}",     metadataText + (schemaHint.Length > 0 ? "\n\nFIELD SCHEMA:\n" + schemaHint : ""))
            .Replace("{content}",      chunk.Content.Length > 6_000 ? chunk.Content[..6_000] : chunk.Content);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userContent)
        };

        try
        {
            var response = await client.CompleteChatAsync(messages,
                new ChatCompletionOptions { MaxOutputTokenCount = 1024, Temperature = 0.0f }, ct);

            var json   = CleanJson(response.Value.Content[0].Text);
            var record = JsonSerializer.Deserialize<ContractRecord>(json, JsonOpts);

            return record is null ? null : record with
            {
                Id                = Guid.NewGuid().ToString("N"),
                DataSourceName    = dataSourceName,
                ConnectorType     = connectorType,
                SourceDocumentUrl = chunk.Url,
                SourceFileName    = chunk.Title,
                SourceMetadata    = new Dictionary<string, string>(chunk.Metadata)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[PP] LLM enrichment failed for '{Title}' from {Src}",
                chunk.Title, dataSourceName);
            return null;
        }
    }

    // ── Direct field mapping ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to build a ContractRecord directly from chunk Metadata without an LLM call.
    ///
    /// Field resolution order per contract field:
    ///   1. Schema-declared key names from MetadataSchema (supports any custom column name)
    ///   2. Well-known hardcoded aliases (ProjectNumber, PROJ_ID, CO_EMAIL, etc.)
    ///
    /// Returns null when not enough anchor data is present, triggering LLM enrichment.
    /// </summary>
    private ContractRecord? TryDirectMapping(
        DocumentChunk chunk,
        string connectorType,
        string dataSourceName,
        IReadOnlyDictionary<string, MetadataFieldDefinition> schema)
    {
        var m = chunk.Metadata;

        // ── Lookup helpers ────────────────────────────────────────────────────

        // Try a list of explicit key names in order; return first non-empty match.
        string Get(params string[] keys)
        {
            foreach (var k in keys)
                if (m.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return string.Empty;
        }

        // Try schema-declared key first (schema field name == metadata key).
        // Falls back through the hardcoded aliases so existing connectors keep working.
        string GetField(string schemaKey, params string[] fallbackKeys)
        {
            // Direct schema key match
            if (m.TryGetValue(schemaKey, out var sv) && !string.IsNullOrWhiteSpace(sv)) return sv;
            // Also check schema field names that share the same Description as schemaKey
            // (allows "ContractNumber" schema key to pick up "ContractNo" metadata key if declared)
            if (schema.TryGetValue(schemaKey, out var fieldDef) && !string.IsNullOrEmpty(fieldDef.Description))
            {
                foreach (var kv in m)
                    if (schema.TryGetValue(kv.Key, out var kDef) &&
                        kDef.Description.Equals(fieldDef.Description, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(kv.Value))
                        return kv.Value;
            }
            return Get(fallbackKeys);
        }

        decimal? GetDecimal(params string[] keys)
        {
            foreach (var k in keys)
                if (m.TryGetValue(k, out var v) &&
                    decimal.TryParse(v, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return d;
            return null;
        }

        DateOnly? GetDate(params string[] keys)
        {
            foreach (var k in keys)
                if (m.TryGetValue(k, out var v) && DateOnly.TryParse(v, out var d)) return d;
            return null;
        }

        // ── Schema-aware field resolution ─────────────────────────────────────
        // For each ContractRecord field, check the declared schema key first,
        // then fall through to standard aliases.

        var contractNumber = GetField("ContractNumber",
            "ProjectNumber", "CONTRACT_NUM", "PROJ_ID", "OpportunityNumber");

        var agencyName = GetField("AgencyName",
            "ClientName", "Client", "Agency", "CLIENT_ID");

        // Require at least one anchor field to avoid empty records
        if (string.IsNullOrEmpty(contractNumber) && string.IsNullOrEmpty(agencyName))
            return null;

        var naicsRaw  = GetField("NAICSCode", "NAICS", "NaicsCode", "NAICS_CODE");
        var naicsList = string.IsNullOrEmpty(naicsRaw)
            ? new List<string>()
            : naicsRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        var cparsOverall = NormaliseCpars(
            GetField("CPARSRatingOverall", "OverallRating", "Rating", "CPARS_OVERALL"));

        var statusRaw = GetField("ProjectStatus", "Status", "PROJ_STATUS");
        var isOngoing = statusRaw.Contains("Active", StringComparison.OrdinalIgnoreCase)
                     || Get("IsOngoing") == "true";

        return new ContractRecord
        {
            DataSourceName          = dataSourceName,
            ConnectorType           = connectorType,
            ContractNumber          = contractNumber,
            Title                   = !string.IsNullOrEmpty(chunk.Title) ? chunk.Title
                                      : GetField("Title", "ProjectName", "ContractTitle", "PROJ_NAME"),
            Description             = GetField("Description", "ProjectDescription", "PROJ_DESC", "Scope"),
            AgencyName              = agencyName,
            AgencyAcronym           = GetField("AgencyAcronym", "Agency"),
            ContractType            = GetField("ContractType", "CONTRACT_TYPE", "Type"),
            ContractValue           = GetDecimal("ContractAmount", "Budget", "ContractValue", "Value", "CONTRACT_AMT"),
            FinalObligatedValue     = GetDecimal("FinalObligatedValue", "TotalValue", "FINAL_AMT"),
            StartDate               = GetDate("StartDate", "START_DATE", "BeginDate"),
            EndDate                 = GetDate("EndDate", "END_DATE", "CompletionDate"),
            IsOngoing               = isOngoing,
            NaicsCodes              = naicsList,
            CPARSRatingOverall      = cparsOverall,
            CPARSRatingQuality      = NormaliseCpars(GetField("CPARSRatingQuality",  "QualityRating",  "CPARS_QUALITY")),
            CPARSRatingSchedule     = NormaliseCpars(GetField("CPARSRatingSchedule", "ScheduleRating", "CPARS_SCHEDULE")),
            CPARSRatingCostControl  = NormaliseCpars(GetField("CPARSRatingCostControl", "CostRating", "CPARS_COST")),
            CPARSRatingManagement   = NormaliseCpars(GetField("CPARSRatingManagement",  "MgmtRating", "CPARS_MGMT")),
            ContractingOfficer      = GetField("ContractingOfficer", "CO", "CO_NAME", "ProjectManager"),
            ContractingOfficerEmail = GetField("COEmail", "ContractingOfficerEmail", "CO_EMAIL"),
            ContractingOfficerPhone = GetField("COPhone", "ContractingOfficerPhone", "CO_PHONE"),
            PerformingEntity        = GetField("PerformingEntity", "Contractor", "PrimeContractor"),
            SourceDocumentUrl       = chunk.Url,
            SourceFileName          = chunk.Title,
            SourceMetadata          = new Dictionary<string, string>(m)
        };
    }

    // ── Schema helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the declared MetadataSchema for a named data source.
    /// Empty dict when the source has no schema declaration.
    /// </summary>
    private IReadOnlyDictionary<string, MetadataFieldDefinition> GetSchema(string dataSourceName)
    {
        var ds = _registry.DataSources.FirstOrDefault(d => d.Name == dataSourceName);
        return (IReadOnlyDictionary<string, MetadataFieldDefinition>?)ds?.MetadataSchema
               ?? new Dictionary<string, MetadataFieldDefinition>();
    }

    /// <summary>
    /// Renders a concise schema description appended to the LLM enrichment prompt
    /// so the model knows the semantic meaning of each metadata field.
    /// Only includes Required and Searchable fields to keep the prompt concise.
    /// </summary>
    private static string BuildSchemaHint(IReadOnlyDictionary<string, MetadataFieldDefinition> schema)
    {
        if (schema.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var (key, def) in schema.Where(kv => kv.Value.Searchable))
        {
            sb.Append($"  {key} ({def.Type}): {def.Description}");
            if (def.AllowedValues.Count > 0)
                sb.Append($" [values: {string.Join(", ", def.AllowedValues)}]");
            if (def.Required)
                sb.Append(" [required]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Normalisation ─────────────────────────────────────────────────────────

    private static string NormaliseCpars(string raw) => raw.ToUpperInvariant() switch
    {
        "EXCEPTIONAL" or "OUTSTANDING" or "5" => "Exceptional",
        "VERY GOOD"   or "VERYGOOD"    or "4" => "Very Good",
        "SATISFACTORY" or "GOOD"       or "3" => "Satisfactory",
        "MARGINAL"     or "FAIR"       or "2" => "Marginal",
        "UNSATISFACTORY" or "POOR"     or "1" => "Unsatisfactory",
        _ => string.Empty
    };

    private static string NormaliseContractNumber(string s) =>
        string.IsNullOrWhiteSpace(s) ? "__no_contract__" : s.ToUpperInvariant().Trim();

    private static string CleanJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```json")) raw = raw[7..];
        else if (raw.StartsWith("```"))  raw = raw[3..];
        if (raw.EndsWith("```")) raw = raw[..^3];
        return raw.Trim();
    }
}
