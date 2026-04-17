using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using SharePointRag.Core.Configuration;
using SharePointRag.PastPerformance.Interfaces;
using SharePointRag.PastPerformance.Models;
using SharePointRag.PastPerformance.Prompts;
using System.Text;
using System.Text.Json;

namespace SharePointRag.PastPerformance.Services;

/// <summary>
/// Drafts proposal-ready Past Performance narratives and volume sections.
///
/// Narrative flow per contract:
///   - Relevance rationale (for capture team)
///   - ~500-word narrative (for the proposal)
///   - Formatted reference block (CO + COR contact)
///
/// Volume flow:
///   - Draft narratives for each ranked contract (parallel)
///   - Generate executive summary paragraph
///   - Identify and flag any data gaps
/// </summary>
public sealed class ProposalDrafter(
    AzureOpenAIClient openAi,
    IOptions<AzureOpenAIOptions> aoaiOpts,
    ILogger<ProposalDrafter> logger) : IProposalDrafter
{
    private readonly AzureOpenAIOptions _aoai = aoaiOpts.Value;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    { PropertyNameCaseInsensitive = true };

    // ── Draft a single narrative ──────────────────────────────────────────────

    public async Task<ContractNarrative> DraftNarrativeAsync(
        ContractRecord contract,
        string solicitationContext,
        CancellationToken ct = default)
    {
        logger.LogDebug("Drafting narrative for contract {N}", contract.ContractNumber);

        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var contractJson = JsonSerializer.Serialize(contract, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var userContent = PastPerformancePrompts.NarrativeDraftUserTemplate
            .Replace("{solicitationContext}", solicitationContext)
            .Replace("{contractJson}",        contractJson);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.NarrativeDraftSystem),
            new UserChatMessage(userContent)
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 1200,
            Temperature         = 0.3f   // slight creativity for compelling prose
        };

        var response = await client.CompleteChatAsync(messages, options, ct);
        var json     = CleanJson(response.Value.Content[0].Text);

        try
        {
            var draft = JsonSerializer.Deserialize<NarrativeDraftResponse>(json, _jsonOpts);
            return new ContractNarrative
            {
                Contract          = contract,
                RelevanceRationale = draft?.RelevanceRationale ?? string.Empty,
                NarrativeText     = draft?.NarrativeText       ?? string.Empty,
                ReferenceBlock    = draft?.ReferenceBlock       ?? BuildFallbackReferenceBlock(contract)
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Narrative JSON parse failed for {N} — using raw text", contract.ContractNumber);
            return new ContractNarrative
            {
                Contract          = contract,
                NarrativeText     = json,
                ReferenceBlock    = BuildFallbackReferenceBlock(contract)
            };
        }
    }

    // ── Draft a complete volume section ───────────────────────────────────────

    public async Task<PastPerformanceVolumeSection> DraftVolumeAsync(
        List<ContractRecord> contracts,
        string solicitationContext,
        CancellationToken ct = default)
    {
        logger.LogInformation("Drafting Past Performance Volume for {N} contracts", contracts.Count);

        // 1. Draft narratives in parallel (max 4 concurrent to respect rate limits)
        var semaphore  = new SemaphoreSlim(4);
        var narratives = new ContractNarrative[contracts.Count];

        var tasks = contracts.Select((c, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync(ct);
            try { narratives[i] = await DraftNarrativeAsync(c, solicitationContext, ct); }
            finally { semaphore.Release(); }
        }, ct)).ToArray();

        await Task.WhenAll(tasks);

        // 2. Executive summary
        var contractSummaries = BuildContractSummaries(contracts);
        var execSummary = await GenerateExecutiveSummaryAsync(
            solicitationContext, contractSummaries, ct);

        // 3. Gap detection
        var gaps = DetectGaps(contracts);

        return new PastPerformanceVolumeSection
        {
            SolicitationReference = solicitationContext.Length > 80
                                    ? solicitationContext[..80] + "…"
                                    : solicitationContext,
            Narratives            = [.. narratives],
            ExecutiveSummary      = execSummary,
            FlaggedGaps           = gaps,
            GeneratedAt           = DateTimeOffset.UtcNow
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string> GenerateExecutiveSummaryAsync(
        string solicitationContext, string contractSummaries, CancellationToken ct)
    {
        var client = openAi.GetChatClient(_aoai.ChatDeployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PastPerformancePrompts.ExecutiveSummarySystem),
            new UserChatMessage(PastPerformancePrompts.ExecutiveSummaryUserTemplate
                .Replace("{solicitationContext}", solicitationContext)
                .Replace("{contractSummaries}",  contractSummaries))
        };

        var response = await client.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = 250, Temperature = 0.4f }, ct);

        return response.Value.Content[0].Text.Trim();
    }

    private static string BuildContractSummaries(List<ContractRecord> contracts)
    {
        var sb = new StringBuilder();
        foreach (var c in contracts)
        {
            sb.AppendLine($"- {c.ContractNumber} | {c.AgencyName} | {c.Title}");
            sb.AppendLine($"  Value: ${c.FinalObligatedValue ?? c.ContractValue:N0} | " +
                          $"Period: {c.StartDate} – {(c.IsOngoing ? "Ongoing" : c.EndDate?.ToString())}");
            if (!string.IsNullOrEmpty(c.CPARSRatingOverall))
                sb.AppendLine($"  CPARS Overall: {c.CPARSRatingOverall}");
        }
        return sb.ToString();
    }

    private static List<string> DetectGaps(List<ContractRecord> contracts)
    {
        var gaps = new List<string>();

        // Flag missing CO contacts (required in most RFPs)
        var missingCo = contracts.Where(c =>
            string.IsNullOrEmpty(c.ContractingOfficerEmail)
            && string.IsNullOrEmpty(c.ContractingOfficerPhone)).ToList();
        if (missingCo.Count > 0)
            gaps.Add($"Missing CO contact info for: {string.Join(", ", missingCo.Select(c => c.ContractNumber))}");

        // Flag missing CPARS ratings
        var missingCpars = contracts.Where(c => string.IsNullOrEmpty(c.CPARSRatingOverall)).ToList();
        if (missingCpars.Count > 0)
            gaps.Add($"No CPARS rating found for: {string.Join(", ", missingCpars.Select(c => c.ContractNumber))} — retrieve from CPARS.gov");

        // Flag very old contracts (>6 years)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var old   = contracts.Where(c =>
            !c.IsOngoing
            && c.EndDate.HasValue
            && (today.DayNumber - c.EndDate.Value.DayNumber) / 365.25 > 6).ToList();
        if (old.Count > 0)
            gaps.Add($"Contracts older than 6 years may not meet recency requirements: {string.Join(", ", old.Select(c => c.ContractNumber))}");

        // Flag contracts without measurable accomplishments
        var noAccomplishments = contracts.Where(c => c.KeyAccomplishments.Count == 0).ToList();
        if (noAccomplishments.Count > 0)
            gaps.Add($"Add measurable accomplishments to: {string.Join(", ", noAccomplishments.Select(c => c.ContractNumber))}");

        return gaps;
    }

    private static string BuildFallbackReferenceBlock(ContractRecord c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Contracting Officer: {c.ContractingOfficer ?? "[VERIFY]"}, " +
                      $"{c.ContractingOfficerPhone ?? "[VERIFY]"}, " +
                      $"{c.ContractingOfficerEmail ?? "[VERIFY]"}");
        sb.AppendLine($"COR: {c.COR ?? "[VERIFY]"}, " +
                      $"{c.CORPhone ?? "[VERIFY]"}, " +
                      $"{c.COREmail ?? "[VERIFY]"}");
        return sb.ToString();
    }

    private static string CleanJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```json")) raw = raw[7..];
        else if (raw.StartsWith("```")) raw = raw[3..];
        if (raw.EndsWith("```")) raw = raw[..^3];
        return raw.Trim();
    }

    // Local DTO for LLM response parsing
    private sealed class NarrativeDraftResponse
    {
        public string RelevanceRationale { get; set; } = string.Empty;
        public string NarrativeText      { get; set; } = string.Empty;
        public string ReferenceBlock     { get; set; } = string.Empty;
    }
}
