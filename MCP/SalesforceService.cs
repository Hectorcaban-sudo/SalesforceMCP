using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SalesforceMcpServer.Models;

namespace SalesforceMcpServer.Services;

/// <summary>
/// Handles all Salesforce REST API interactions: auth, SOQL query, CRUD, and CSV export.
/// </summary>
public class SalesforceService : IDisposable
{
    private readonly ILogger<SalesforceService> _logger;
    private readonly SchemaService _schemaService;
    private readonly HttpClient _httpClient;

    private string? _accessToken;
    private string? _instanceUrl;
    private DateTime _tokenExpiry = DateTime.MinValue;

    private const string ApiVersion = "v59.0";

    public SalesforceService(ILogger<SalesforceService> logger, SchemaService schemaService)
    {
        _logger = logger;
        _schemaService = schemaService;
        _httpClient = new HttpClient();
    }

    // ── Authentication ────────────────────────────────────────────────────────

    private SalesforceConfig LoadConfig()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "salesforce-config.json");
        if (!File.Exists(configPath))
            configPath = Path.Combine(Directory.GetCurrentDirectory(), "salesforce-config.json");

        if (!File.Exists(configPath))
            throw new FileNotFoundException($"salesforce-config.json not found. Expected at: {configPath}");

        var json = File.ReadAllText(configPath);
        return JsonConvert.DeserializeObject<SalesforceConfig>(json)
               ?? throw new InvalidOperationException("Failed to parse salesforce-config.json");
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return;

        var config = LoadConfig();
        _instanceUrl = config.InstanceUrl;

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type", "password"),
            new KeyValuePair<string,string>("client_id", config.ClientId),
            new KeyValuePair<string,string>("client_secret", config.ClientSecret),
            new KeyValuePair<string,string>("username", config.Username),
            new KeyValuePair<string,string>("password", config.Password + config.SecurityToken),
        });

        var response = await _httpClient.PostAsync(
            $"{config.InstanceUrl}/services/oauth2/token", form);

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Salesforce auth failed: {body}");

        var token = JsonConvert.DeserializeObject<TokenResponse>(body)
                    ?? throw new Exception("Invalid token response");

        _accessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.InstanceUrl))
            _instanceUrl = token.InstanceUrl;

        _tokenExpiry = DateTime.UtcNow.AddHours(1);
        _logger.LogInformation("Salesforce authenticated. Instance: {Url}", _instanceUrl);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativeUrl, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{_instanceUrl}/services/data/{ApiVersion}/{relativeUrl}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (body != null)
        {
            var json = JsonConvert.SerializeObject(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return req;
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public async Task<QueryResult> QueryAsync(ParsedQuery parsed)
    {
        await EnsureAuthenticatedAsync();

        var objectSchema = parsed.ObjectName != null
            ? _schemaService.FindObject(parsed.ObjectName)
            : null;

        // Build SOQL
        var fields = objectSchema != null
            ? _schemaService.ResolveFields(objectSchema, parsed.Fields)
            : parsed.Fields.Any() ? parsed.Fields : new List<string> { "Id", "Name" };

        var soql = BuildSoql(
            objectSchema?.ObjectName ?? parsed.ObjectName ?? "Account",
            fields,
            parsed.WhereClause,
            parsed.OrderBy,
            parsed.Limit ?? 200);

        return await ExecuteSoqlAsync(soql, objectSchema?.ObjectName ?? parsed.ObjectName, fields);
    }

    public async Task<QueryResult> ExecuteRawSoqlAsync(string soql)
    {
        await EnsureAuthenticatedAsync();
        return await ExecuteSoqlAsync(soql, null, null);
    }

    private async Task<QueryResult> ExecuteSoqlAsync(string soql, string? objectName, List<string>? fields)
    {
        _logger.LogInformation("SOQL: {Soql}", soql);

        var encoded = Uri.EscapeDataString(soql);
        var req = BuildRequest(HttpMethod.Get, $"query?q={encoded}");
        var resp = await _httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var errors = JsonConvert.DeserializeObject<List<SalesforceErrorResponse>>(body);
            return new QueryResult
            {
                Success = false,
                Error = errors?.FirstOrDefault()?.Message ?? body
            };
        }

        var result = JsonConvert.DeserializeObject<SalesforceQueryResponse>(body)
                     ?? new SalesforceQueryResponse();

        // Flatten records (remove 'attributes' key)
        var cleaned = result.Records.Select(r =>
        {
            var dict = new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase);
            dict.Remove("attributes");
            return dict;
        }).ToList();

        return new QueryResult
        {
            Success = true,
            TotalSize = result.TotalSize,
            Records = cleaned,
            ObjectName = objectName,
            Fields = fields
        };
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<CrudResult> CreateAsync(ParsedQuery parsed)
    {
        await EnsureAuthenticatedAsync();

        var schema = parsed.ObjectName != null ? _schemaService.FindObject(parsed.ObjectName) : null;
        var objectName = schema?.ObjectName ?? parsed.ObjectName;
        if (string.IsNullOrEmpty(objectName))
            return new CrudResult { Success = false, Error = "Object name is required for create." };

        // Resolve field names via schema
        var payload = ResolvePayloadFields(schema, parsed.FieldValues);

        var req = BuildRequest(HttpMethod.Post, $"sobjects/{objectName}", payload);
        var resp = await _httpClient.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var errs = JsonConvert.DeserializeObject<List<SalesforceErrorResponse>>(body);
            return new CrudResult { Success = false, Error = errs?.FirstOrDefault()?.Message ?? body };
        }

        var created = JsonConvert.DeserializeObject<Dictionary<string, object?>>(body);
        return new CrudResult
        {
            Success = true,
            Id = created?["id"]?.ToString(),
            Message = $"Created {objectName} successfully."
        };
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<CrudResult> UpdateAsync(ParsedQuery parsed)
    {
        await EnsureAuthenticatedAsync();

        var schema = parsed.ObjectName != null ? _schemaService.FindObject(parsed.ObjectName) : null;
        var objectName = schema?.ObjectName ?? parsed.ObjectName;
        if (string.IsNullOrEmpty(objectName))
            return new CrudResult { Success = false, Error = "Object name is required for update." };

        if (string.IsNullOrEmpty(parsed.RecordId))
            return new CrudResult { Success = false, Error = "Record ID is required for update." };

        var payload = ResolvePayloadFields(schema, parsed.FieldValues);
        payload.Remove("Id"); // Id cannot be in PATCH body

        var req = BuildRequest(HttpMethod.Patch, $"sobjects/{objectName}/{parsed.RecordId}", payload);
        var resp = await _httpClient.SendAsync(req);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || resp.IsSuccessStatusCode)
            return new CrudResult { Success = true, Id = parsed.RecordId, Message = $"Updated {objectName} {parsed.RecordId} successfully." };

        var body = await resp.Content.ReadAsStringAsync();
        var errs = JsonConvert.DeserializeObject<List<SalesforceErrorResponse>>(body);
        return new CrudResult { Success = false, Error = errs?.FirstOrDefault()?.Message ?? body };
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task<CrudResult> DeleteAsync(ParsedQuery parsed)
    {
        await EnsureAuthenticatedAsync();

        var schema = parsed.ObjectName != null ? _schemaService.FindObject(parsed.ObjectName) : null;
        var objectName = schema?.ObjectName ?? parsed.ObjectName;
        if (string.IsNullOrEmpty(objectName))
            return new CrudResult { Success = false, Error = "Object name is required for delete." };

        if (string.IsNullOrEmpty(parsed.RecordId))
            return new CrudResult { Success = false, Error = "Record ID is required for delete." };

        var req = BuildRequest(HttpMethod.Delete, $"sobjects/{objectName}/{parsed.RecordId}");
        var resp = await _httpClient.SendAsync(req);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || resp.IsSuccessStatusCode)
            return new CrudResult { Success = true, Id = parsed.RecordId, Message = $"Deleted {objectName} {parsed.RecordId} successfully." };

        var body = await resp.Content.ReadAsStringAsync();
        var errs = JsonConvert.DeserializeObject<List<SalesforceErrorResponse>>(body);
        return new CrudResult { Success = false, Error = errs?.FirstOrDefault()?.Message ?? body };
    }

    // ── CSV Export ────────────────────────────────────────────────────────────

    public async Task<string> ExportToCsvAsync(ParsedQuery parsed, string? outputPath = null)
    {
        var result = await QueryAsync(parsed);
        if (!result.Success) throw new Exception(result.Error);
        if (!result.Records.Any()) return "No records to export.";

        var path = outputPath ?? Path.Combine(
            Path.GetTempPath(),
            $"{parsed.ObjectName ?? "export"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");

        using var writer = new StreamWriter(path, false, Encoding.UTF8);

        // Header
        var headers = result.Records.First().Keys.ToList();
        await writer.WriteLineAsync(CsvLine(headers));

        // Rows
        foreach (var rec in result.Records)
            await writer.WriteLineAsync(CsvLine(headers.Select(h => rec.TryGetValue(h, out var v) ? v?.ToString() ?? "" : "")));

        return path;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSoql(string objectName, List<string> fields, string? where, string? orderBy, int limit)
    {
        var sb = new StringBuilder($"SELECT {string.Join(", ", fields)} FROM {objectName}");
        if (!string.IsNullOrEmpty(where)) sb.Append($" WHERE {where}");
        if (!string.IsNullOrEmpty(orderBy)) sb.Append($" ORDER BY {orderBy}");
        sb.Append($" LIMIT {limit}");
        return sb.ToString();
    }

    private Dictionary<string, object?> ResolvePayloadFields(
        ObjectSchema? schema, Dictionary<string, object?> rawValues)
    {
        if (schema == null) return new Dictionary<string, object?>(rawValues, StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rawValues)
        {
            var field = _schemaService.FindField(schema, key);
            resolved[field?.FieldName ?? key] = value;
        }
        return resolved;
    }

    private static string CsvLine(IEnumerable<string?> values)
    {
        return string.Join(",", values.Select(v =>
        {
            if (v == null) return "";
            if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
                return $"\"{v.Replace("\"", "\"\"")}\"";
            return v;
        }));
    }

    public void Dispose() => _httpClient.Dispose();
}
