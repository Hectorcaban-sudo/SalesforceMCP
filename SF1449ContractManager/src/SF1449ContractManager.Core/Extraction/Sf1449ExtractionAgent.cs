using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SF1449ContractManager.Core.Models;

namespace SF1449ContractManager.Core.Extraction;

public interface IContractExtractionAgent
{
    /// <summary>
    /// Runs the AI extraction agent over the tagged PDF text and returns a fully
    /// populated (but not-yet-persisted) contract graph, plus the field-level
    /// confidence/provenance metadata used to drive the review UI.
    /// </summary>
    Task<ExtractionOutcome> ExtractAsync(string taggedPdfText, string sourceFileName, CancellationToken ct = default);
}

public record ExtractionOutcome(Sf1449Contract Contract, string RawModelJson);

/// <summary>
/// Thin wrapper around a Microsoft Agent Framework <see cref="AIAgent"/> that is
/// instructed (see <see cref="ExtractionPrompts"/>) to read SF-1449 text and return
/// strict JSON, which is then mapped onto the EF Core entity graph via reflection
/// (property names in the JSON match Sf1449Contract's CLR property names 1:1).
/// </summary>
public class Sf1449ExtractionAgent : IContractExtractionAgent
{
    private readonly AIAgent _agent;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <param name="chatClient">
    /// Any Microsoft.Extensions.AI IChatClient - Azure OpenAI, OpenAI, or a local model
    /// via an OpenAI-compatible endpoint all work. Register this in DI (see
    /// Program.cs in the Web project) and it flows in here.
    /// </param>
    public Sf1449ExtractionAgent(IChatClient chatClient)
    {
        _agent = chatClient.CreateAIAgent(
            name: "SF1449Extractor",
            instructions: ExtractionPrompts.BuildSystemInstructions());
    }

    public async Task<ExtractionOutcome> ExtractAsync(string taggedPdfText, string sourceFileName, CancellationToken ct = default)
    {
        var userPrompt = $"""
            Source file: {sourceFileName}

            Extract the SF-1449 package below into the required JSON shape.

            --- BEGIN DOCUMENT TEXT ---
            {taggedPdfText}
            --- END DOCUMENT TEXT ---
            """;

        var response = await _agent.RunAsync(userPrompt, cancellationToken: ct);
        var rawJson = ExtractJsonPayload(response.Text ?? response.ToString() ?? string.Empty);

        var parsed = JsonSerializer.Deserialize<Sf1449ExtractionResponse>(rawJson, JsonOptions)
                     ?? new Sf1449ExtractionResponse();

        var contract = new Sf1449Contract
        {
            SourcePdfFileName = sourceFileName,
            ExtractedAtUtc = DateTime.UtcNow,
            Status = ContractStatus.PendingReview,
        };

        ApplyHeaderFields(contract, parsed.HeaderFields);
        ApplyLineItems(contract, parsed.LineItems);
        ApplyClauses(contract, parsed.Clauses);

        return new ExtractionOutcome(contract, rawJson);
    }

    /// <summary>Models sometimes wrap JSON in ```json fences despite instructions - strip if present.</summary>
    private static string ExtractJsonPayload(string modelText)
    {
        var text = modelText.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                text = text[(firstNewline + 1)..lastFence].Trim();
            }
        }
        return text;
    }

    private static void ApplyHeaderFields(Sf1449Contract contract, Dictionary<string, ExtractedField> fields)
    {
        var props = typeof(Sf1449Contract)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (fieldName, extracted) in fields)
        {
            if (!props.TryGetValue(fieldName, out var prop) || !prop.CanWrite)
                continue;

            var converted = ConvertValue(extracted.Value, prop.PropertyType);
            if (converted is not null)
                prop.SetValue(contract, converted);

            contract.FieldExtractions.Add(new FieldExtraction
            {
                FieldName = prop.Name,
                DisplayLabel = ExtractionPrompts.HeaderFieldCatalogue
                    .FirstOrDefault(f => f.FieldName.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    .Description,
                ExtractedValueRaw = extracted.Value,
                Confidence = extracted.Confidence,
                SourcePageNumber = extracted.SourcePage
            });
        }
    }

    private static object? ConvertValue(string? raw, Type targetType)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (underlying == typeof(string)) return raw;
            if (underlying == typeof(bool)) return raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            if (underlying == typeof(decimal)) return decimal.Parse(raw.Replace("$", "").Replace(",", "").Trim(), CultureInfo.InvariantCulture);
            if (underlying == typeof(int)) return int.Parse(raw.Trim(), CultureInfo.InvariantCulture);
            if (underlying == typeof(DateTime)) return DateTime.Parse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None);
            if (underlying.IsEnum) return Enum.Parse(underlying, raw.Trim(), ignoreCase: true);
        }
        catch
        {
            // Extraction gave us a value we couldn't parse cleanly (bad date format, stray text,
            // etc). We deliberately swallow this rather than fail the whole extraction - the
            // raw value is still preserved on the FieldExtraction row for a human to fix up
            // on the review screen.
            return null;
        }

        return null;
    }

    private static void ApplyLineItems(Sf1449Contract contract, List<ExtractedLineItem> items)
    {
        var order = 0;
        foreach (var item in items)
        {
            contract.LineItems.Add(new ContractLineItem
            {
                SortOrder = order++,
                ItemNumber = item.ItemNumber,
                Description = item.Description,
                Quantity = TryDecimal(item.Quantity),
                Unit = item.Unit,
                UnitPrice = TryDecimal(item.UnitPrice),
                Amount = TryDecimal(item.Amount),
                FrequencyOfService = item.FrequencyOfService,
                PerformanceLocation = item.PerformanceLocation
            });
        }
    }

    private static void ApplyClauses(Sf1449Contract contract, List<ExtractedClause> clauses)
    {
        foreach (var clause in clauses)
        {
            contract.Clauses.Add(new ContractClause
            {
                ClauseNumber = clause.ClauseNumber,
                Title = clause.Title,
                EffectiveDate = clause.EffectiveDate,
                Category = Enum.TryParse<ClauseCategory>(clause.Category, true, out var cat) ? cat : ClauseCategory.Other,
                IncorporationType = Enum.TryParse<ClauseIncorporationType>(clause.IncorporationType, true, out var inc) ? inc : ClauseIncorporationType.ByReference,
                Section = Enum.TryParse<ClauseSection>(clause.Section, true, out var sec) ? sec : ClauseSection.ContractClauses,
                IsChecked = clause.IsChecked
            });
        }
    }

    private static decimal? TryDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return decimal.TryParse(raw.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }
}
