using Newtonsoft.Json;

namespace SalesforceMcpServer.Models;

public class SalesforceConfig
{
    public string InstanceUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string SecurityToken { get; set; } = "";
}

public class ObjectSchema
{
    [JsonProperty("objectName")]
    public string ObjectName { get; set; } = "";

    [JsonProperty("label")]
    public string Label { get; set; } = "";

    [JsonProperty("labelPlural")]
    public string LabelPlural { get; set; } = "";

    [JsonProperty("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonProperty("fields")]
    public List<FieldSchema> Fields { get; set; } = new();
}

public class FieldSchema
{
    [JsonProperty("fieldName")]
    public string FieldName { get; set; } = "";

    [JsonProperty("label")]
    public string Label { get; set; } = "";

    [JsonProperty("type")]
    public string Type { get; set; } = "";

    [JsonProperty("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonProperty("required")]
    public bool Required { get; set; }

    [JsonProperty("updateable")]
    public bool Updateable { get; set; } = true;

    [JsonProperty("createable")]
    public bool Createable { get; set; } = true;

    [JsonProperty("filterable")]
    public bool Filterable { get; set; } = true;
}

public class QueryResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int TotalSize { get; set; }
    public List<Dictionary<string, object?>> Records { get; set; } = new();
    public string? ObjectName { get; set; }
    public List<string>? Fields { get; set; }
}

public class CrudResult
{
    public bool Success { get; set; }
    public string? Id { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
}

public class SalesforceRecord
{
    [JsonProperty("attributes")]
    public RecordAttributes? Attributes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?> Fields { get; set; } = new();
}

public class RecordAttributes
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("url")]
    public string? Url { get; set; }
}

public class SalesforceQueryResponse
{
    [JsonProperty("totalSize")]
    public int TotalSize { get; set; }

    [JsonProperty("done")]
    public bool Done { get; set; }

    [JsonProperty("records")]
    public List<Dictionary<string, object?>> Records { get; set; } = new();
}

public class SalesforceErrorResponse
{
    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("errorCode")]
    public string? ErrorCode { get; set; }
}

public class TokenResponse
{
    [JsonProperty("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonProperty("instance_url")]
    public string InstanceUrl { get; set; } = "";
}

public class ParsedQuery
{
    public string Operation { get; set; } = ""; // query, create, update, delete, export
    public string? ObjectName { get; set; }
    public List<string> Fields { get; set; } = new();
    public string? WhereClause { get; set; }
    public Dictionary<string, object?> FieldValues { get; set; } = new();
    public string? RecordId { get; set; }
    public int? Limit { get; set; }
    public string? OrderBy { get; set; }
    public string? NaturalLanguage { get; set; }
}
