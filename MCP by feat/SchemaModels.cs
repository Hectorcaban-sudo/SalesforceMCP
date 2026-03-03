using System.Text.Json.Serialization;

namespace McpServer.Models;

/// <summary>
/// Represents a Salesforce object schema loaded from a JSON file
/// </summary>
public class SalesforceObjectSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("labelPlural")]
    public string LabelPlural { get; set; } = string.Empty;

    [JsonPropertyName("keyPrefix")]
    public string? KeyPrefix { get; set; }

    [JsonPropertyName("fields")]
    public List<SalesforceFieldSchema> Fields { get; set; } = new();

    [JsonPropertyName("relationships")]
    public List<SalesforceRelationship> Relationships { get; set; } = new();
}

public class SalesforceFieldSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("nillable")]
    public bool Nillable { get; set; }

    [JsonPropertyName("updateable")]
    public bool Updateable { get; set; }

    [JsonPropertyName("createable")]
    public bool Createable { get; set; }

    [JsonPropertyName("filterable")]
    public bool Filterable { get; set; }

    [JsonPropertyName("sortable")]
    public bool Sortable { get; set; }

    [JsonPropertyName("picklistValues")]
    public List<PicklistValue> PicklistValues { get; set; } = new();
}

public class PicklistValue
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

public class SalesforceRelationship
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("referenceTo")]
    public List<string> ReferenceTo { get; set; } = new();

    [JsonPropertyName("relationshipName")]
    public string? RelationshipName { get; set; }
}

/// <summary>
/// Result wrapper for MCP tool calls
/// </summary>
public class McpResult
{
    public bool Success { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
    public int TotalSize { get; set; }
    public bool Done { get; set; }

    public static McpResult Ok(object data, int totalSize = 0, bool done = true) =>
        new() { Success = true, Data = data, TotalSize = totalSize, Done = done };

    public static McpResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Parsed intent from natural language input
/// </summary>
public class ParsedIntent
{
    public string Operation { get; set; } = string.Empty; // query, create, update, delete, export, describe
    public string ObjectName { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = new();
    public string? WhereClause { get; set; }
    public Dictionary<string, object?> FieldValues { get; set; } = new();
    public string? RecordId { get; set; }
    public int? Limit { get; set; }
    public string? OrderBy { get; set; }
    public bool OrderDescending { get; set; }
    public string? RawSoql { get; set; }
}
