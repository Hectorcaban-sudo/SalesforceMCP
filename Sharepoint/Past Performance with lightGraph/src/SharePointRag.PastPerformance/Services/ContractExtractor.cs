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
/// Routes to one of two extraction strategies based on the connector type
/// that produced each chunk:
///
/// ── Document sources (SharePoint, Custom) ────────────────────────────────────
///   Groups chunks by Title (document), concatenates content, sends to GPT-4o
///   with the full unstructured extraction prompt. Handles PPQs, CPARS printouts,
///   proposal volumes — any free-text past performance document.
///
/// ── Structured sources (SqlDatabase, Deltek, Excel) ──────────────────────────
///   Each chunk already carries rich Metadata from the connector (SQL columns,
///   Deltek API fields, Excel header values). Uses a separate "enrichment" prompt
///   that tells GPT-4o to map those structured fields to the ContractRecord schema
///   rather than extract from narrative prose. This is faster, cheaper, and more
///   accurate for structured data.
///
/// Both paths stamp DataSourceName + ConnectorType on every ContractRecord so
/// downstream services (scorer, drafter, controller) know the provenance.
///
/// Deduplication: records with the same ContractNumber (case-insensitive) are
/// collapsed, keeping the one with the highest relevance score. Structured source
/// records are preferred over document-extracted ones when values conflict.
/// </summary>
public sealed class LlmContractExtractor(
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    ILogger<LlmContractExtractor> logger) : IContractExtractor
{
    private readonly AzureOpenAIOptions _aoai = aoaiOpts.Value;

    // Connector types treated as structured sources
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

        // Partition chunks by extraction strategy
        var documentChunks   = new List<RetrievedChunk>();
        var structuredChunks = new List<RetrievedChunk>();

        foreach (var rc in chunks)
        {
            var connectorType = rc.Chunk.Metadata.TryGetValue("ConnectorType", out var ct2)
                ? ct2
                : rc.Chunk.DataSourceName;   // fall back to DataSourceName as hint

            if (StructuredTypes.Contains(connectorType))
                structuredChunks.Add(rc);
            else
                documentChunks.Add(rc);
        }

        var allRecords = new List<ContractRecord>();

        // ── Path 1: Document extraction (SharePoint, Custom) ──────────────────
        if (documentChunks.Count > 0)
        {
            var extracted = await ExtractFromDocumentsAsync(documentChunks, ct);
            allRecords.AddRange(extracted);
        }

        // ── Path 2: Structured enrichment (SQL, Deltek, Excel) ────────────────
        if (structuredChunks.Count > 0)
        {
            var enriched = await EnrichFromStructuredSourcesAsync(structuredChunks, ct);
            allRecords.AddRange(enriched);
        }

        // ── Deduplicate by ContractNumber ─────────────────────────────────────
        // Prefer structured source records (already have clean field data).
        return allRecords
            .GroupBy(r => NormaliseContractNumber(r.ContractNumber))
            .Select(g =>
            {
                var preferred = g.FirstOrDefault(r => StructuredTypes.Contains(r.ConnectorType))
                                ?? g.OrderByDescending(r => r.RelevanceScore).First();
                return preferred;
            })
            .ToList();
    }

    // ── Document extraction ───────────────────────────────────────────────────

    private async Task<List<ContractRecord>> ExtractFromDocumentsAsync(
        List<RetrievedChunk> chunks, CancellationToken ct)
    {
        // Group by Title+DataSourceName so all chunks of the same document are sent together
        var byDocument = chunks
            .GroupBy(c => $"{c.Chunk.DataSourceName}::{c.Chunk.Title}")
            .ToList();

        var results = new List<ContractRecord>();

        foreach (var group in byDocument)
        {
            var title         = group.First().Chunk.Title;
            var url           = group.First().Chunk.Url;
            var dataSourceName = group.First().Chunk.DataSourceName;
            var connectorType  = group.First().Chunk.Metadata.TryGetValue("ConnectorType", out var ct2)
                ? ct2 : "SharePoint";

            var combinedText = new StringBuilder();
            foreach (var rc in group.OrderBy(c => c.Chunk.ChunkIndex))
                combinedText.AppendLine(rc.Chunk.Content).AppendLine();

            logger.LogDebug("[PP] Document extraction: {Title} ({N} chunks) from {Src}",
                title, group.Count(), dataSourceName);

            var records = await CallExtractionLlmAsync(
                combinedText.ToString(), title, url, dataSourceName, connectorType, ct);

            results.AddRange(records);
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
            logger.LogWarning(ex, "[PP] Document extraction JSON parse failed for {T}. JSON: {J}",
                sourceTitle, json.Length > 400 ? json[..400] : json);
            return [];
        }
    }

    // ── Structured enrichment ─────────────────────────────────────────────────

    private async Task<List<ContractRecord>> EnrichFromStructuredSourcesAsync(
        List<RetrievedChunk> chunks, CancellationToken ct)
    {
        // Each chunk from a structured source represents ONE logical record
        // (a SQL row, a Deltek project, an Excel row). Process in parallel (max 4).
        var semaphore = new SemaphoreSlim(4);
        var tasks = chunks.Select(rc => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct);
            try { return await EnrichSingleRecordAsync(rc, ct); }
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

        // First try direct mapping — many structured sources have enough data
        // without an LLM call. If key GovCon fields are present, map them directly.
        var direct = TryDirectMapping(chunk, connectorType, dataSourceName);
        if (direct is not null)
        {
            logger.LogDebug("[PP] Direct-mapped record '{Title}' from {Src}",
                chunk.Title, dataSourceName);
            return direct;
        }

        // Fall back to LLM enrichment for complex structured data
        logger.LogDebug("[PP] LLM enrichment for '{Title}' from {Src}", chunk.Title, dataSourceName);

        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var metadataText = string.Join("\n",
            chunk.Metadata.Select(kv => $"  {kv.Key}: {kv.Value}"));

        var systemPrompt = PastPerformancePrompts.StructuredEnrichmentSystem
            .Replace("{connectorType}", connectorType);

        var userContent = PastPerformancePrompts.StructuredEnrichmentUserTemplate
            .Replace("{connectorType}", connectorType)
            .Replace("{sourceName}",   dataSourceName)
            .Replace("{title}",        chunk.Title)
            .Replace("{url}",          chunk.Url)
            .Replace("{metadata}",     metadataText)
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

            var json = CleanJson(response.Value.Content[0].Text);
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

    /// <summary>
    /// Attempts to build a ContractRecord purely from well-known metadata keys
    /// without an LLM call. Works when the connector populates standard column names.
    /// Returns null if insufficient data is present (triggering LLM fallback).
    /// </summary>
    private static ContractRecord? TryDirectMapping(
        DocumentChunk chunk, string connectorType, string dataSourceName)
    {
        var m = chunk.Metadata;

        string Get(params string[] keys)
        {
            foreach (var k in keys)
                if (m.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return string.Empty;
        }

        decimal? GetDecimal(params string[] keys)
        {
            foreach (var k in keys)
                if (m.TryGetValue(k, out var v) && decimal.TryParse(v,
                    System.Globalization.NumberStyles.Any,
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

        // ── Deltek field mappings ─────────────────────────────────────────────
        var contractNumber = Get("ProjectNumber", "ContractNumber", "CONTRACT_NUM",
                                 "PROJ_ID", "OpportunityNumber");

        // Only direct-map if we have enough anchor data
        if (string.IsNullOrEmpty(contractNumber)
            && string.IsNullOrEmpty(Get("AgencyName", "ClientName", "Agency")))
            return null;

        var naicsRaw  = Get("NAICSCode", "NAICS", "NaicsCode");
        var naicsList = string.IsNullOrEmpty(naicsRaw)
            ? new List<string>()
            : naicsRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        var cparsOverall = NormaliseCpars(Get("CPARSRatingOverall", "OverallRating", "Rating"));

        return new ContractRecord
        {
            DataSourceName    = dataSourceName,
            ConnectorType     = connectorType,
            ContractNumber    = contractNumber,
            Title             = !string.IsNullOrEmpty(chunk.Title) ? chunk.Title
                                : Get("ProjectName", "ContractTitle", "PROJ_NAME", "OpportunityName"),
            Description       = Get("Description", "ProjectDescription", "PROJ_DESC", "Scope"),
            AgencyName        = Get("AgencyName", "ClientName", "Client", "Agency"),
            AgencyAcronym     = Get("AgencyAcronym", "Agency"),
            ContractType      = Get("ContractType", "CONTRACT_TYPE", "Type"),
            ContractValue     = GetDecimal("ContractAmount", "Budget", "ContractValue", "Value"),
            FinalObligatedValue = GetDecimal("FinalObligatedValue", "TotalValue", "FINAL_AMT"),
            StartDate         = GetDate("StartDate", "START_DATE", "BeginDate"),
            EndDate           = GetDate("EndDate", "END_DATE", "CompletionDate"),
            IsOngoing         = Get("ProjectStatus", "Status")
                                    .Contains("Active", StringComparison.OrdinalIgnoreCase)
                                || Get("IsOngoing") == "true",
            NaicsCodes        = naicsList,
            CPARSRatingOverall = cparsOverall,
            ContractingOfficer = Get("ContractingOfficer", "CO", "ProjectManager"),
            ContractingOfficerEmail = Get("COEmail", "ContractingOfficerEmail"),
            ContractingOfficerPhone = Get("COPhone", "ContractingOfficerPhone"),
            PerformingEntity  = Get("PerformingEntity", "Contractor", "PrimeContractor"),
            SourceDocumentUrl = chunk.Url,
            SourceFileName    = chunk.Title,
            SourceMetadata    = new Dictionary<string, string>(m)
        };
    }

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
