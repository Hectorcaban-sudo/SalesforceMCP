using McpServer.Models;
using System.Text.RegularExpressions;

namespace McpServer.Infrastructure.Salesforce;

public interface INaturalLanguageParser
{
    ParsedIntent Parse(string input, string? objectHint = null);
}

/// <summary>
/// Parses simple natural language queries into structured Salesforce operations.
/// Supports: "get/show/find/list/query", "create/add/insert/new", 
///           "update/edit/change/modify/set", "delete/remove", "export"
/// </summary>
public class NaturalLanguageParser : INaturalLanguageParser
{
    private readonly SchemaRegistry _registry;
    private readonly ILogger<NaturalLanguageParser> _logger;

    private static readonly Dictionary<string, string> OperationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get"] = "query", ["show"] = "query", ["find"] = "query", ["list"] = "query",
        ["fetch"] = "query", ["retrieve"] = "query", ["search"] = "query", ["query"] = "query",
        ["select"] = "query", ["give me"] = "query", ["display"] = "query",
        ["create"] = "create", ["add"] = "create", ["insert"] = "create", ["new"] = "create",
        ["make"] = "create",
        ["update"] = "update", ["edit"] = "update", ["change"] = "update", ["modify"] = "update",
        ["set"] = "update", ["patch"] = "update",
        ["delete"] = "delete", ["remove"] = "delete", ["erase"] = "delete",
        ["export"] = "export", ["download"] = "export", ["csv"] = "export",
        ["describe"] = "describe", ["schema"] = "describe", ["fields"] = "describe",
        ["what fields"] = "describe",
    };

    private static readonly string[] ComparisonWords = { "where", "that have", "with", "whose", "having" };
    private static readonly string[] LimitWords = { "top", "first", "last" };

    public NaturalLanguageParser(SchemaRegistry registry, ILogger<NaturalLanguageParser> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public ParsedIntent Parse(string input, string? objectHint = null)
    {
        _logger.LogDebug("Parsing NL input: {Input}", input);
        input = input.Trim();

        // If it looks like raw SOQL, pass through
        if (input.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
            return new ParsedIntent { Operation = "query", RawSoql = input };

        var intent = new ParsedIntent();

        // Detect operation
        intent.Operation = DetectOperation(input);

        // Detect object name
        var objectName = objectHint ?? DetectObjectName(input);
        intent.ObjectName = objectName ?? string.Empty;

        var schema = string.IsNullOrEmpty(intent.ObjectName) ? null : _registry.GetSchema(intent.ObjectName);
        if (schema != null)
            intent.ObjectName = schema.Name; // normalize to API name

        // Detect fields
        intent.Fields = DetectFields(input, schema);

        // Detect WHERE conditions
        intent.WhereClause = DetectWhereClause(input, schema);

        // Detect LIMIT
        intent.Limit = DetectLimit(input);

        // Detect ORDER
        DetectOrder(input, schema, intent);

        // Detect field values for create/update
        if (intent.Operation is "create" or "update")
            intent.FieldValues = DetectFieldValues(input, schema);

        // Detect record ID
        intent.RecordId = DetectRecordId(input);

        _logger.LogDebug("Parsed intent: Op={Op} Obj={Obj} Fields={Fields}",
            intent.Operation, intent.ObjectName, string.Join(",", intent.Fields));

        return intent;
    }

    private string DetectOperation(string input)
    {
        var lower = input.ToLowerInvariant();

        foreach (var kv in OperationKeywords.OrderByDescending(k => k.Key.Length))
        {
            if (lower.Contains(kv.Key))
                return kv.Value;
        }

        return "query"; // default
    }

    private string? DetectObjectName(string input)
    {
        var lower = input.ToLowerInvariant();

        // Try exact and fuzzy match against all known objects
        foreach (var schema in _registry.GetAllSchemas().OrderByDescending(s => s.Name.Length))
        {
            if (lower.Contains(schema.Name.ToLowerInvariant()) ||
                lower.Contains(schema.Label.ToLowerInvariant()) ||
                lower.Contains(schema.LabelPlural.ToLowerInvariant()))
            {
                return schema.Name;
            }
        }

        // Try to extract a word that could be an object (capitalized or after keyword)
        var afterKeywords = new[] { "for ", "from ", "in ", "on ", "about " };
        foreach (var kw in afterKeywords)
        {
            var idx = lower.IndexOf(kw);
            if (idx >= 0)
            {
                var rest = input[(idx + kw.Length)..].Split(' ').FirstOrDefault();
                if (!string.IsNullOrEmpty(rest))
                {
                    var resolved = _registry.ResolveObjectName(rest.Trim('s', '.', ','));
                    if (resolved != null) return resolved;
                }
            }
        }

        return null;
    }

    private List<string> DetectFields(string input, Models.SalesforceObjectSchema? schema)
    {
        if (schema == null) return new() { "Id", "Name" };

        var lower = input.ToLowerInvariant();

        // "all fields" → return first 20
        if (lower.Contains("all fields") || lower.Contains("all columns"))
            return schema.Fields.Select(f => f.Name).Take(20).ToList();

        // Find fields mentioned by name or label
        var found = new List<string>();
        foreach (var field in schema.Fields)
        {
            if (lower.Contains(field.Name.ToLowerInvariant()) ||
                lower.Contains(field.Label.ToLowerInvariant()))
            {
                found.Add(field.Name);
            }
        }

        return found.Count > 0 ? found : _registry.GetDefaultFields(schema);
    }

    private string? DetectWhereClause(string input, Models.SalesforceObjectSchema? schema)
    {
        if (schema == null) return null;
        var lower = input.ToLowerInvariant();

        foreach (var word in ComparisonWords)
        {
            var idx = lower.IndexOf(word);
            if (idx < 0) continue;

            var condition = input[(idx + word.Length)..].Trim();

            // Try to parse "FieldName = value", "FieldName contains X", "FieldName like X"
            var whereMatch = Regex.Match(condition,
                @"(?<field>\w+)\s*(?<op>=|!=|>|<|>=|<=|contains|like|starts with|ends with)\s*(?<val>['""]?[\w\s@.-]+['""]?)",
                RegexOptions.IgnoreCase);

            if (whereMatch.Success)
            {
                var fieldInput = whereMatch.Groups["field"].Value;
                var op = whereMatch.Groups["op"].Value.ToLowerInvariant();
                var val = whereMatch.Groups["val"].Value.Trim('\'', '"');

                var fieldName = _registry.ResolveFieldName(schema, fieldInput) ?? fieldInput;

                return op switch
                {
                    "contains" or "like" => $"{fieldName} LIKE '%{val}%'",
                    "starts with" => $"{fieldName} LIKE '{val}%'",
                    "ends with" => $"{fieldName} LIKE '%{val}'",
                    _ => $"{fieldName} {op.ToUpper()} '{val}'"
                };
            }

            // Simple: "Name = 'Acme'" already in condition
            if (condition.Contains("=") || condition.Contains(">") || condition.Contains("<"))
                return condition.Split(new[] { " and ", " or ", " limit ", " order " },
                    StringSplitOptions.IgnoreCase).First().Trim();
        }

        return null;
    }

    private int? DetectLimit(string input)
    {
        var match = Regex.Match(input, @"\b(top|first|limit)\s+(\d+)\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[2].Value, out var n))
            return n;

        match = Regex.Match(input, @"\b(\d+)\s+(records?|rows?|items?|results?)\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var n2))
            return n2;

        return null;
    }

    private void DetectOrder(string input, Models.SalesforceObjectSchema? schema, ParsedIntent intent)
    {
        var lower = input.ToLowerInvariant();
        var match = Regex.Match(lower, @"\border(?:ed)? by\s+(\w+)(?:\s+(asc|desc))?", RegexOptions.IgnoreCase);
        if (match.Success && schema != null)
        {
            var fieldInput = match.Groups[1].Value;
            intent.OrderBy = _registry.ResolveFieldName(schema, fieldInput) ?? fieldInput;
            intent.OrderDescending = match.Groups[2].Value.Equals("desc", StringComparison.OrdinalIgnoreCase);
        }

        if (lower.Contains("newest") || lower.Contains("most recent") || lower.Contains("latest"))
        {
            intent.OrderBy ??= "CreatedDate";
            intent.OrderDescending = true;
        }
        if (lower.Contains("oldest"))
        {
            intent.OrderBy ??= "CreatedDate";
            intent.OrderDescending = false;
        }
    }

    private Dictionary<string, object?> DetectFieldValues(string input, Models.SalesforceObjectSchema? schema)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (schema == null) return values;

        // Match patterns like: "Name = 'Acme'" or "Name: Acme" or "set Name to Acme"
        var patterns = new[]
        {
            @"(?<field>\w+)\s*[=:]\s*['""]?(?<val>[^'""=,\n]+)['""]?",
            @"set\s+(?<field>\w+)\s+to\s+['""]?(?<val>[^'""=,\n]+)['""]?"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match m in Regex.Matches(input, pattern, RegexOptions.IgnoreCase))
            {
                var fieldInput = m.Groups["field"].Value;
                var val = m.Groups["val"].Value.Trim().Trim('\'', '"', ' ');
                var fieldName = _registry.ResolveFieldName(schema, fieldInput);
                if (fieldName != null && !values.ContainsKey(fieldName))
                    values[fieldName] = val;
            }
        }

        return values;
    }

    private string? DetectRecordId(string input)
    {
        // Salesforce IDs are 15 or 18 character alphanumeric
        var match = Regex.Match(input, @"\b([a-zA-Z0-9]{15}|[a-zA-Z0-9]{18})\b");
        return match.Success ? match.Value : null;
    }
}
