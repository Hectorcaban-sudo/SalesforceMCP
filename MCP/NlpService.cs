using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SalesforceMcpServer.Models;

namespace SalesforceMcpServer.Services;

/// <summary>
/// Converts natural language requests into structured ParsedQuery objects.
/// Examples:
///   "show me all accounts in California"
///   "create a new lead named John Smith at Acme Corp"
///   "update opportunity id 0016X000 set amount to 50000"
///   "delete contact with id 003XX000"
///   "export all open opportunities to csv"
/// </summary>
public class NlpService
{
    private readonly ILogger<NlpService> _logger;
    private readonly SchemaService _schemaService;

    // Operation keyword patterns
    private static readonly string[] QueryKeywords = { "show", "get", "find", "list", "fetch", "retrieve", "search", "display", "what", "who", "how many", "count" };
    private static readonly string[] CreateKeywords = { "create", "add", "insert", "new", "make" };
    private static readonly string[] UpdateKeywords = { "update", "change", "modify", "edit", "set", "rename" };
    private static readonly string[] DeleteKeywords = { "delete", "remove", "trash", "archive" };
    private static readonly string[] ExportKeywords = { "export", "download", "csv", "extract", "dump" };

    public NlpService(ILogger<NlpService> logger, SchemaService schemaService)
    {
        _logger = logger;
        _schemaService = schemaService;
    }

    public ParsedQuery Parse(string naturalLanguage)
    {
        var input = naturalLanguage.Trim();
        var lower = input.ToLowerInvariant();

        var result = new ParsedQuery { NaturalLanguage = input };

        // Detect operation
        result.Operation = DetectOperation(lower);

        // Detect object
        result.ObjectName = DetectObject(lower);

        // Detect record ID (e.g., "id 0016X0000...", "with id ...", "#...")
        result.RecordId = DetectRecordId(lower);

        // Detect LIMIT
        result.Limit = DetectLimit(lower);

        // Detect ORDER BY
        result.OrderBy = DetectOrderBy(lower, result.ObjectName);

        // Detect fields to select
        result.Fields = DetectFields(lower, result.ObjectName);

        // Detect WHERE conditions
        result.WhereClause = DetectWhereClause(lower, result.ObjectName, result.RecordId);

        // Detect field values for CREATE/UPDATE
        if (result.Operation == "create" || result.Operation == "update")
            result.FieldValues = DetectFieldValues(input, result.ObjectName);

        _logger.LogDebug("Parsed NL '{Input}' => Op:{Op} Obj:{Obj} Where:{Where} Limit:{Limit}",
            input, result.Operation, result.ObjectName, result.WhereClause, result.Limit);

        return result;
    }

    private string DetectOperation(string lower)
    {
        if (ExportKeywords.Any(k => lower.Contains(k))) return "export";
        if (DeleteKeywords.Any(k => lower.Contains(k))) return "delete";
        if (UpdateKeywords.Any(k => lower.Contains(k))) return "update";
        if (CreateKeywords.Any(k => lower.Contains(k))) return "create";
        return "query";
    }

    private string? DetectObject(string lower)
    {
        // Try to find a schema match anywhere in the sentence
        foreach (var schema in _schemaService.GetAllSchemas())
        {
            var namesToCheck = new List<string> { schema.ObjectName.ToLower(), schema.Label.ToLower(), schema.LabelPlural.ToLower() };
            namesToCheck.AddRange(schema.Aliases.Select(a => a.ToLower()));

            foreach (var name in namesToCheck)
            {
                // word-boundary match
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(name)}\b"))
                    return schema.ObjectName;
            }
        }
        return null;
    }

    private string? DetectRecordId(string lower)
    {
        // Salesforce IDs are 15 or 18 alphanumeric chars
        var match = Regex.Match(lower, @"\b([a-z0-9]{15,18})\b", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;

        // "id <value>"
        var idMatch = Regex.Match(lower, @"\bid\s+([a-z0-9]{15,18})\b", RegexOptions.IgnoreCase);
        if (idMatch.Success) return idMatch.Groups[1].Value;

        return null;
    }

    private int? DetectLimit(string lower)
    {
        // "first 10", "top 5", "limit 100", "last 20"
        var patterns = new[]
        {
            @"\bfirst\s+(\d+)\b",
            @"\btop\s+(\d+)\b",
            @"\blimit\s+(\d+)\b",
            @"\blast\s+(\d+)\b",
            @"\b(\d+)\s+records?\b",
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(lower, p);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
        }
        return null;
    }

    private string? DetectOrderBy(string lower, string? objectName)
    {
        // "order by <field>", "sorted by <field>", "by <field>"
        var match = Regex.Match(lower, @"\b(?:order(?:ed)?\s+by|sort(?:ed)?\s+by)\s+(\w+(?:\s+(?:asc|desc))?)\b");
        if (!match.Success) return null;

        var rawField = match.Groups[1].Value.Trim();
        if (objectName != null)
        {
            var schema = _schemaService.FindObject(objectName);
            if (schema != null)
            {
                var words = rawField.Split(' ');
                var field = _schemaService.FindField(schema, words[0]);
                if (field != null)
                {
                    var dir = words.Length > 1 ? words[1].ToUpper() : "ASC";
                    return $"{field.FieldName} {dir}";
                }
            }
        }
        return rawField;
    }

    private List<string> DetectFields(string lower, string? objectName)
    {
        var fields = new List<string>();
        if (objectName == null) return fields;

        var schema = _schemaService.FindObject(objectName);
        if (schema == null) return fields;

        // Look for field names/aliases mentioned in the sentence
        foreach (var field in schema.Fields)
        {
            var namesToCheck = new List<string> { field.FieldName.ToLower(), field.Label.ToLower() };
            namesToCheck.AddRange(field.Aliases.Select(a => a.ToLower()));

            foreach (var name in namesToCheck)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(name)}\b"))
                {
                    if (!fields.Contains(field.FieldName))
                        fields.Add(field.FieldName);
                    break;
                }
            }
        }

        return fields;
    }

    private string? DetectWhereClause(string lower, string? objectName, string? recordId)
    {
        if (objectName == null) return null;
        var schema = _schemaService.FindObject(objectName);
        var clauses = new List<string>();

        if (recordId != null)
        {
            clauses.Add($"Id = '{recordId}'");
        }

        if (schema != null)
        {
            // Pattern: "<field> is/= <value>", "<field> contains <value>", "where <field> <op> <value>"
            foreach (var field in schema.Fields.Where(f => f.Filterable))
            {
                var namesToCheck = new List<string> { field.FieldName.ToLower(), field.Label.ToLower() };
                namesToCheck.AddRange(field.Aliases.Select(a => a.ToLower()));

                foreach (var name in namesToCheck)
                {
                    // "<name> is/= '<value>'"
                    var eqMatch = Regex.Match(lower, $@"\b{Regex.Escape(name)}\s+(?:is\s+|=\s*|equals?\s+)['""]?([^'""]+?)['""]?(?:\s|$)");
                    if (eqMatch.Success)
                    {
                        var val = eqMatch.Groups[1].Value.Trim();
                        clauses.Add(FormatWhereValue(field, val));
                        break;
                    }

                    // "in <name>" → name LIKE '%value%'
                    var containsMatch = Regex.Match(lower, $@"\b{Regex.Escape(name)}\s+contains?\s+['""]?([^'""]+?)['""]?(?:\s|$)");
                    if (containsMatch.Success)
                    {
                        var val = containsMatch.Groups[1].Value.Trim();
                        clauses.Add($"{field.FieldName} LIKE '%{val}%'");
                        break;
                    }
                }
            }

            // "in <state/city>" → BillingState / BillingCity etc.
            var inMatch = Regex.Match(lower, @"\bin\s+([a-zA-Z\s]{2,30})(?:\s|$)");
            if (inMatch.Success && clauses.Count == 0)
            {
                var loc = inMatch.Groups[1].Value.Trim();
                // Try to find a state or city field
                var stateField = schema.Fields.FirstOrDefault(f =>
                    f.FieldName.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                    f.Aliases.Any(a => a.Contains("state", StringComparison.OrdinalIgnoreCase)));
                if (stateField != null)
                    clauses.Add($"{stateField.FieldName} = '{loc}'");
            }

            // Status/Stage keywords
            var statusField = schema.Fields.FirstOrDefault(f =>
                f.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
                f.FieldName.Equals("StageName", StringComparison.OrdinalIgnoreCase));

            if (statusField != null)
            {
                var statusPatterns = new Dictionary<string, string>
                {
                    { @"\bopen\b", "Open" },
                    { @"\bclosed\b", "Closed" },
                    { @"\bwon\b", "Closed Won" },
                    { @"\blost\b", "Closed Lost" },
                    { @"\bactive\b", "Active" },
                    { @"\bnew\b", "New" },
                };
                foreach (var (pattern, value) in statusPatterns)
                {
                    if (Regex.IsMatch(lower, pattern) && !clauses.Any(c => c.Contains(statusField.FieldName)))
                        clauses.Add($"{statusField.FieldName} = '{value}'");
                }
            }
        }

        return clauses.Count > 0 ? string.Join(" AND ", clauses) : null;
    }

    private static string FormatWhereValue(FieldSchema field, string value)
    {
        return field.Type?.ToLower() switch
        {
            "boolean" => $"{field.FieldName} = {value.ToLower()}",
            "double" or "currency" or "integer" or "percent" =>
                decimal.TryParse(value, out _) ? $"{field.FieldName} = {value}" : $"{field.FieldName} = '{value}'",
            _ => $"{field.FieldName} = '{value}'"
        };
    }

    private Dictionary<string, object?> DetectFieldValues(string input, string? objectName)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (objectName == null) return values;

        var schema = _schemaService.FindObject(objectName);
        if (schema == null) return values;

        var lower = input.ToLowerInvariant();

        foreach (var field in schema.Fields.Where(f => f.Createable || f.Updateable))
        {
            var namesToCheck = new List<string> { field.FieldName.ToLower(), field.Label.ToLower() };
            namesToCheck.AddRange(field.Aliases.Select(a => a.ToLower()));

            foreach (var name in namesToCheck)
            {
                // "<name> <is/=/to> <value>" or "set <name> to <value>"
                var patterns = new[]
                {
                    $@"\b{Regex.Escape(name)}\s+(?:is\s+|=\s*|to\s+|:\s*)['""]?([^'""]+?)['""]?(?:\s+(?:and|,|\.|with)|\s*$)",
                    $@"\bset\s+{Regex.Escape(name)}\s+to\s+['""]?([^'""]+?)['""]?(?:\s|$)",
                };

                foreach (var pattern in patterns)
                {
                    var m = Regex.Match(lower, pattern);
                    if (m.Success)
                    {
                        var raw = m.Groups[1].Value.Trim();
                        values[field.FieldName] = ParseFieldValue(field, raw);
                        break;
                    }
                }
            }
        }

        // Special: "named <value>" → Name field
        var namedMatch = Regex.Match(input, @"\bnamed?\s+['""]?([^'""]+?)['""]?(?:\s|$)", RegexOptions.IgnoreCase);
        if (namedMatch.Success)
        {
            var nameField = schema.Fields.FirstOrDefault(f =>
                f.FieldName.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                f.FieldName.Equals("LastName", StringComparison.OrdinalIgnoreCase));
            if (nameField != null && !values.ContainsKey(nameField.FieldName))
                values[nameField.FieldName] = namedMatch.Groups[1].Value.Trim();
        }

        return values;
    }

    private static object? ParseFieldValue(FieldSchema field, string raw)
    {
        return field.Type?.ToLower() switch
        {
            "boolean" => raw is "true" or "yes" or "1",
            "double" or "currency" or "percent" =>
                decimal.TryParse(raw, out var d) ? d : raw,
            "integer" =>
                int.TryParse(raw, out var i) ? i : raw,
            _ => raw
        };
    }
}
