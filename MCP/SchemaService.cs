using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SalesforceMcpServer.Models;

namespace SalesforceMcpServer.Services;

/// <summary>
/// Loads and resolves Salesforce object/field schemas from JSON files in the /schemas directory.
/// No objects or fields are hardcoded — everything is driven by the JSON files.
/// </summary>
public class SchemaService
{
    private readonly ILogger<SchemaService> _logger;
    private readonly Dictionary<string, ObjectSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _schemasPath;

    public SchemaService(ILogger<SchemaService> logger)
    {
        _logger = logger;

        // Look for schemas folder next to the executable, or fall back to current dir
        var exeDir = AppContext.BaseDirectory;
        _schemasPath = Path.Combine(exeDir, "schemas");
        if (!Directory.Exists(_schemasPath))
            _schemasPath = Path.Combine(Directory.GetCurrentDirectory(), "schemas");

        LoadSchemas();
    }

    private void LoadSchemas()
    {
        if (!Directory.Exists(_schemasPath))
        {
            _logger.LogWarning("Schemas directory not found at {Path}", _schemasPath);
            return;
        }

        foreach (var file in Directory.GetFiles(_schemasPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var schema = JsonConvert.DeserializeObject<ObjectSchema>(json);
                if (schema != null)
                {
                    _schemas[schema.ObjectName] = schema;
                    _logger.LogInformation("Loaded schema: {Object}", schema.ObjectName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load schema from {File}", file);
            }
        }

        _logger.LogInformation("Total schemas loaded: {Count}", _schemas.Count);
    }

    public void ReloadSchemas()
    {
        _schemas.Clear();
        LoadSchemas();
    }

    public IEnumerable<ObjectSchema> GetAllSchemas() => _schemas.Values;

    public ObjectSchema? FindObject(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return null;

        // Direct match
        if (_schemas.TryGetValue(nameOrAlias, out var direct)) return direct;

        // Match by alias or label (case-insensitive)
        var lower = nameOrAlias.ToLowerInvariant().Trim();
        return _schemas.Values.FirstOrDefault(s =>
            s.ObjectName.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
            s.Label.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
            s.LabelPlural.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
            s.Aliases.Any(a => a.Equals(lower, StringComparison.OrdinalIgnoreCase)));
    }

    public FieldSchema? FindField(ObjectSchema obj, string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return null;
        var lower = nameOrAlias.ToLowerInvariant().Trim();
        return obj.Fields.FirstOrDefault(f =>
            f.FieldName.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
            f.Label.Equals(lower, StringComparison.OrdinalIgnoreCase) ||
            f.Aliases.Any(a => a.Equals(lower, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Resolve a list of user-supplied field names/aliases to actual API field names.
    /// If input is empty, returns all fields on the object.
    /// </summary>
    public List<string> ResolveFields(ObjectSchema obj, IEnumerable<string>? requestedFields)
    {
        var requested = requestedFields?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (requested == null || requested.Count == 0)
            return obj.Fields.Select(f => f.FieldName).ToList();

        var resolved = new List<string>();
        foreach (var rf in requested)
        {
            var field = FindField(obj, rf);
            if (field != null)
                resolved.Add(field.FieldName);
            else
                resolved.Add(rf); // pass through unknown names — Salesforce will error descriptively
        }
        return resolved;
    }

    public string DescribeSchema()
    {
        if (!_schemas.Any()) return "No schemas loaded. Add JSON files to the schemas/ directory.";

        var lines = new List<string> { $"Available Salesforce Objects ({_schemas.Count}):\n" };
        foreach (var s in _schemas.Values)
        {
            lines.Add($"  Object: {s.ObjectName} ({s.Label})");
            if (s.Aliases.Any()) lines.Add($"    Aliases: {string.Join(", ", s.Aliases)}");
            lines.Add($"    Fields ({s.Fields.Count}):");
            foreach (var f in s.Fields)
            {
                var aliases = f.Aliases.Any() ? $" [aliases: {string.Join(", ", f.Aliases)}]" : "";
                lines.Add($"      - {f.FieldName} ({f.Type}) \"{f.Label}\"{aliases}");
            }
            lines.Add("");
        }
        return string.Join("\n", lines);
    }
}
