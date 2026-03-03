using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using SalesforceMcpServer.Models;
using SalesforceMcpServer.Services;

namespace SalesforceMcpServer.Tools;

[McpServerToolType]
public class SalesforceTools
{
    private readonly SalesforceService _sf;
    private readonly SchemaService _schema;
    private readonly NlpService _nlp;

    public SalesforceTools(SalesforceService sf, SchemaService schema, NlpService nlp)
    {
        _sf = sf;
        _schema = schema;
        _nlp = nlp;
    }

    // ── Natural Language Query ────────────────────────────────────────────────

    [McpServerTool, Description(
        "Query, create, update, delete, or export Salesforce data using natural language. " +
        "Examples: 'show me all accounts in California', " +
        "'find open opportunities over $50000', " +
        "'create a new lead named John Smith at Acme Corp', " +
        "'update contact 003XX0000012345 set phone to 555-1234', " +
        "'delete account 001XX0000012345', " +
        "'export all contacts to csv'. " +
        "No SOQL knowledge required.")]
    public async Task<string> SalesforceNaturalLanguage(
        [Description("Natural language instruction describing what to do with Salesforce data")]
        string instruction)
    {
        try
        {
            var parsed = _nlp.Parse(instruction);

            return parsed.Operation switch
            {
                "create" => FormatCrudResult(await _sf.CreateAsync(parsed)),
                "update" => FormatCrudResult(await _sf.UpdateAsync(parsed)),
                "delete" => FormatCrudResult(await _sf.DeleteAsync(parsed)),
                "export" => await HandleExport(parsed),
                _ => FormatQueryResult(await _sf.QueryAsync(parsed))
            };
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Raw SOQL ──────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Execute a raw SOQL query against Salesforce. " +
        "Use this when you need full SOQL control. " +
        "Example: \"SELECT Id, Name, Amount FROM Opportunity WHERE StageName = 'Closed Won' LIMIT 50\"")]
    public async Task<string> SalesforceSoqlQuery(
        [Description("A valid SOQL query string")]
        string soql)
    {
        try
        {
            var result = await _sf.ExecuteRawSoqlAsync(soql);
            return FormatQueryResult(result);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Create Record ─────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Create a new Salesforce record. " +
        "Provide the Salesforce object name (or alias from schema) and field values as JSON. " +
        "Example: objectName='Account', fields='{\"Name\":\"Acme\",\"BillingCity\":\"Chicago\"}'")]
    public async Task<string> SalesforceCreateRecord(
        [Description("Salesforce object name or alias (e.g., 'Account', 'contact', 'opportunity')")]
        string objectName,
        [Description("JSON object of field names/aliases to values, e.g. {\"Name\":\"Acme\",\"Phone\":\"555-1234\"}")]
        string fieldsJson)
    {
        try
        {
            var fields = JsonConvert.DeserializeObject<Dictionary<string, object?>>(fieldsJson)
                         ?? throw new ArgumentException("Invalid fields JSON");

            var parsed = new ParsedQuery
            {
                Operation = "create",
                ObjectName = objectName,
                FieldValues = fields
            };

            return FormatCrudResult(await _sf.CreateAsync(parsed));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Update Record ─────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Update an existing Salesforce record by ID. " +
        "Provide the object name, the 15 or 18 character Salesforce record ID, and fields to update as JSON. " +
        "Example: objectName='Opportunity', recordId='006XX0000012345', fields='{\"Amount\":75000}'")]
    public async Task<string> SalesforceUpdateRecord(
        [Description("Salesforce object name or alias")]
        string objectName,
        [Description("15 or 18 character Salesforce record ID")]
        string recordId,
        [Description("JSON object of field names/aliases to update values")]
        string fieldsJson)
    {
        try
        {
            var fields = JsonConvert.DeserializeObject<Dictionary<string, object?>>(fieldsJson)
                         ?? throw new ArgumentException("Invalid fields JSON");

            var parsed = new ParsedQuery
            {
                Operation = "update",
                ObjectName = objectName,
                RecordId = recordId,
                FieldValues = fields
            };

            return FormatCrudResult(await _sf.UpdateAsync(parsed));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Delete Record ─────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Delete a Salesforce record by ID. " +
        "Example: objectName='Contact', recordId='003XX0000012345'")]
    public async Task<string> SalesforceDeleteRecord(
        [Description("Salesforce object name or alias")]
        string objectName,
        [Description("15 or 18 character Salesforce record ID")]
        string recordId)
    {
        try
        {
            var parsed = new ParsedQuery
            {
                Operation = "delete",
                ObjectName = objectName,
                RecordId = recordId
            };

            return FormatCrudResult(await _sf.DeleteAsync(parsed));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Export to CSV ─────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Query Salesforce records and export them as a CSV file. " +
        "Provide a natural language description of what to export. " +
        "Optionally specify an output file path. " +
        "Returns the path to the generated CSV file. " +
        "Example: query='all accounts in New York', outputPath='/tmp/accounts.csv'")]
    public async Task<string> SalesforceExportCsv(
        [Description("Natural language description of records to export, e.g. 'all open opportunities'")]
        string query,
        [Description("Optional output file path. If not provided, a temp file is used.")]
        string? outputPath = null)
    {
        try
        {
            var parsed = _nlp.Parse(query);
            parsed.Operation = "query"; // force query mode for export
            var path = await _sf.ExportToCsvAsync(parsed, outputPath);
            return $"CSV exported successfully to: {path}";
        }
        catch (Exception ex)
        {
            return $"Error exporting CSV: {ex.Message}";
        }
    }

    // ── Schema Tools ──────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "List all available Salesforce objects and their fields loaded from JSON schema files. " +
        "Use this to discover what objects and fields are available before querying.")]
    public string SalesforceListSchema()
    {
        return _schema.DescribeSchema();
    }

    [McpServerTool, Description(
        "Reload Salesforce schemas from the schemas/ directory. " +
        "Use this after adding or modifying schema JSON files without restarting the server.")]
    public string SalesforceReloadSchemas()
    {
        _schema.ReloadSchemas();
        return $"Schemas reloaded.\n\n{_schema.DescribeSchema()}";
    }

    [McpServerTool, Description(
        "Describe a specific Salesforce object's fields, types, and aliases. " +
        "Example: objectName='Account' or objectName='opportunity'")]
    public string SalesforceDescribeObject(
        [Description("Salesforce object name or alias to describe")]
        string objectName)
    {
        var schema = _schema.FindObject(objectName);
        if (schema == null) return $"Object '{objectName}' not found in loaded schemas. Use SalesforceListSchema to see available objects.";

        var lines = new List<string>
        {
            $"Object: {schema.ObjectName}",
            $"Label: {schema.Label} / {schema.LabelPlural}",
        };
        if (schema.Aliases.Any()) lines.Add($"Aliases: {string.Join(", ", schema.Aliases)}");
        lines.Add($"\nFields ({schema.Fields.Count}):");
        foreach (var f in schema.Fields)
        {
            var flags = new List<string>();
            if (f.Required) flags.Add("required");
            if (!f.Createable) flags.Add("not-createable");
            if (!f.Updateable) flags.Add("read-only");
            var aliases = f.Aliases.Any() ? $" [aliases: {string.Join(", ", f.Aliases)}]" : "";
            var flagStr = flags.Any() ? $" ({string.Join(", ", flags)})" : "";
            lines.Add($"  {f.FieldName,-35} {f.Type,-15} \"{f.Label}\"{aliases}{flagStr}");
        }
        return string.Join("\n", lines);
    }

    // ── Formatting Helpers ────────────────────────────────────────────────────

    private static string FormatQueryResult(QueryResult result)
    {
        if (!result.Success) return $"Query failed: {result.Error}";
        if (!result.Records.Any()) return "No records found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {result.TotalSize} record(s){(result.ObjectName != null ? $" ({result.ObjectName})" : "")}:\n");

        foreach (var (rec, i) in result.Records.Select((r, i) => (r, i + 1)))
        {
            sb.AppendLine($"[{i}]");
            foreach (var (key, val) in rec)
                sb.AppendLine($"  {key}: {val}");
        }
        return sb.ToString();
    }

    private static string FormatCrudResult(CrudResult result)
    {
        if (!result.Success) return $"Operation failed: {result.Error}";
        var id = result.Id != null ? $" (ID: {result.Id})" : "";
        return $"{result.Message}{id}";
    }

    private async Task<string> HandleExport(ParsedQuery parsed)
    {
        parsed.Operation = "query";
        var path = await _sf.ExportToCsvAsync(parsed);
        return $"Export complete. CSV saved to: {path}";
    }
}
