// ============================================================
//  GovConRAG.Ingestion — Source Adapters
//  SharePoint (webhook + reconcile), Database, Excel, Custom
// ============================================================

using GovConRAG.Core.Models;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GovConRAG.Ingestion.Adapters;

// ── Adapter Contract ──────────────────────────────────────────

public interface ISourceAdapter
{
    DocumentSource SourceType { get; }

    /// <summary>Fetch raw text and metadata for a given source reference.</summary>
    Task<RawDocument?> FetchAsync(string sourceRef, Dictionary<string, string>? meta, CancellationToken ct = default);

    /// <summary>Enumerate ALL documents (for reconciliation / full scans).</summary>
    IAsyncEnumerable<RawDocument> EnumerateAsync(string rootRef, CancellationToken ct = default);
}

public sealed class RawDocument
{
    public string   SourceRef    { get; set; } = "";
    public string   Title        { get; set; } = "";
    public string   Content      { get; set; } = "";
    public string   MimeType     { get; set; } = "text/plain";
    public long     SizeBytes    { get; set; }
    public string   ContentHash  { get; set; } = "";
    public DateTime LastModified { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();

    public static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// ── SharePoint Adapter ────────────────────────────────────────
// Uses Microsoft Graph API for real SharePoint access.
// Register an Azure AD app with Sites.Read.All permission.

public sealed class SharePointAdapter : ISourceAdapter
{
    public DocumentSource SourceType => DocumentSource.SharePoint;

    private readonly HttpClient     _http;
    private readonly string         _tenantId;
    private readonly string         _clientId;
    private readonly string         _clientSecret;
    private readonly ILogger<SharePointAdapter> _logger;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public SharePointAdapter(
        HttpClient http,
        string tenantId, string clientId, string clientSecret,
        ILogger<SharePointAdapter> logger)
    {
        _http         = http;
        _tenantId     = tenantId;
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _logger       = logger;
    }

    public async Task<RawDocument?> FetchAsync(
        string sourceRef, Dictionary<string, string>? meta, CancellationToken ct = default)
    {
        // sourceRef format: siteId|driveId|itemId
        var parts = sourceRef.Split('|');
        if (parts.Length < 3) return null;

        try
        {
            await EnsureTokenAsync(ct);
            var (siteId, driveId, itemId) = (parts[0], parts[1], parts[2]);

            // Fetch metadata
            var metaUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/items/{itemId}";
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var metaResp = await _http.GetAsync(metaUrl, ct);
            if (!metaResp.IsSuccessStatusCode) return null;

            var metaJson = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync(ct));
            var name     = metaJson.RootElement.GetProperty("name").GetString() ?? "";
            var size     = metaJson.RootElement.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

            // Fetch content
            var contentUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/items/{itemId}/content";
            var contentResp = await _http.GetAsync(contentUrl, ct);
            if (!contentResp.IsSuccessStatusCode) return null;

            var bytes   = await contentResp.Content.ReadAsByteArrayAsync(ct);
            var text    = ExtractText(bytes, name);
            var hash    = RawDocument.ComputeHash(text);

            return new RawDocument
            {
                SourceRef    = sourceRef,
                Title        = name,
                Content      = text,
                MimeType     = GetMimeType(name),
                SizeBytes    = size,
                ContentHash  = hash,
                LastModified = DateTime.UtcNow,
                Metadata     = new Dictionary<string, string>
                {
                    ["siteId"]  = siteId,
                    ["driveId"] = driveId,
                    ["itemId"]  = itemId
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch SharePoint item {Ref}", sourceRef);
            return null;
        }
    }

    public async IAsyncEnumerable<RawDocument> EnumerateAsync(
        string rootRef, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // rootRef = siteId|driveId|folderId
        await EnsureTokenAsync(ct);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        var parts     = rootRef.Split('|');
        var siteId    = parts[0];
        var driveId   = parts[1];
        var folderId  = parts.Length > 2 ? parts[2] : "root";
        var nextUrl   = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/items/{folderId}/children?$top=100";

        while (nextUrl != null && !ct.IsCancellationRequested)
        {
            var resp = await _http.GetAsync(nextUrl, ct);
            if (!resp.IsSuccessStatusCode) break;

            var json   = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var values = json.RootElement.GetProperty("value");

            foreach (var item in values.EnumerateArray())
            {
                if (item.TryGetProperty("folder", out _)) continue; // skip folders (recurse separately)

                var itemId = item.GetProperty("id").GetString()!;
                var name   = item.GetProperty("name").GetString()!;
                var size   = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

                var doc = await FetchAsync($"{siteId}|{driveId}|{itemId}", null, ct);
                if (doc != null) yield return doc;
            }

            nextUrl = json.RootElement.TryGetProperty("@odata.nextLink", out var nl)
                ? nl.GetString() : null;
        }
    }

    // ── SharePoint Webhook Handler ────────────────────────────

    /// <summary>
    /// Called by the API controller when SharePoint POSTs a change notification.
    /// Returns the list of changed item IDs to re-ingest.
    /// </summary>
    public async Task<List<string>> ProcessWebhookNotificationAsync(
        string siteId, string driveId, string subscriptionId,
        string deltaToken, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

        var deltaUrl = string.IsNullOrEmpty(deltaToken)
            ? $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/root/delta"
            : $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/root/delta(token='{deltaToken}')";

        var changedIds = new List<string>();
        var resp       = await _http.GetAsync(deltaUrl, ct);
        if (!resp.IsSuccessStatusCode) return changedIds;

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
        {
            if (item.TryGetProperty("folder", out _)) continue;
            changedIds.Add($"{siteId}|{driveId}|{item.GetProperty("id").GetString()}");
        }

        return changedIds;
    }

    // ── Token Management ──────────────────────────────────────

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry) return;

        var tokenUrl  = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";
        var formData  = new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = _clientId,
            ["client_secret"] = _clientSecret,
            ["scope"]         = "https://graph.microsoft.com/.default"
        };

        var tokenResp = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(formData), ct);
        var tokenJson = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync(ct));

        _accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = tokenJson.RootElement.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
    }

    private static string GetMimeType(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf"  => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".txt"  => "text/plain",
            ".md"   => "text/markdown",
            _       => "application/octet-stream"
        };

    private static string ExtractText(byte[] bytes, string fileName)
    {
        // In production: use PdfPig for PDF, DocumentFormat.OpenXml for docx
        // For this blueprint, return UTF-8 text fallback
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return $"[Binary content: {fileName}]"; }
    }
}

// ── Database Adapter ──────────────────────────────────────────

public sealed class DatabaseAdapter : ISourceAdapter
{
    public DocumentSource SourceType => DocumentSource.Database;

    private readonly DbProviderFactory _factory;
    private readonly string            _connectionString;
    private readonly ILogger<DatabaseAdapter> _logger;

    public DatabaseAdapter(DbProviderFactory factory, string connectionString,
        ILogger<DatabaseAdapter> logger)
    {
        _factory          = factory;
        _connectionString = connectionString;
        _logger           = logger;
    }

    /// <summary>
    /// sourceRef format: "table:tableName|idCol:id|textCols:col1,col2"
    /// </summary>
    public async Task<RawDocument?> FetchAsync(
        string sourceRef, Dictionary<string, string>? meta, CancellationToken ct = default)
    {
        var parts      = ParseRef(sourceRef);
        var table      = parts["table"];
        var idCol      = parts["idCol"];
        var textCols   = parts["textCols"].Split(',');
        var id         = meta?.GetValueOrDefault("id") ?? parts.GetValueOrDefault("id") ?? "";

        using var conn = _factory.CreateConnection()!;
        conn.ConnectionString = _connectionString;
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(",", textCols)} FROM {table} WHERE {idCol} = @id";
        var param = cmd.CreateParameter();
        param.ParameterName = "@id";
        param.Value = id;
        cmd.Parameters.Add(param);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var sb = new StringBuilder();
        for (int i = 0; i < textCols.Length; i++)
        {
            if (!await reader.IsDBNullAsync(i, ct))
                sb.AppendLine($"{textCols[i]}: {reader.GetValue(i)}");
        }

        var content = sb.ToString();
        return new RawDocument
        {
            SourceRef    = sourceRef,
            Title        = $"{table}:{id}",
            Content      = content,
            MimeType     = "text/plain",
            SizeBytes    = content.Length,
            ContentHash  = RawDocument.ComputeHash(content),
            LastModified = DateTime.UtcNow,
            Metadata     = new Dictionary<string, string> { ["table"] = table, ["id"] = id }
        };
    }

    public async IAsyncEnumerable<RawDocument> EnumerateAsync(
        string rootRef, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var parts    = ParseRef(rootRef);
        var table    = parts["table"];
        var idCol    = parts["idCol"];
        var textCols = parts["textCols"].Split(',');

        using var conn = _factory.CreateConnection()!;
        conn.ConnectionString = _connectionString;
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {idCol},{string.Join(",", textCols)} FROM {table}";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id  = reader.GetValue(0)?.ToString() ?? "";
            var sb  = new StringBuilder();
            for (int i = 1; i <= textCols.Length; i++)
            {
                if (!await reader.IsDBNullAsync(i, ct))
                    sb.AppendLine($"{textCols[i - 1]}: {reader.GetValue(i)}");
            }
            var content = sb.ToString();
            if (string.IsNullOrWhiteSpace(content)) continue;

            yield return new RawDocument
            {
                SourceRef   = $"{rootRef}|id:{id}",
                Title       = $"{table}:{id}",
                Content     = content,
                ContentHash = RawDocument.ComputeHash(content),
                LastModified = DateTime.UtcNow,
                Metadata    = new Dictionary<string, string> { ["id"] = id }
            };
        }
    }

    private static Dictionary<string, string> ParseRef(string sourceRef)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in sourceRef.Split('|'))
        {
            var kv = part.Split(':', 2);
            if (kv.Length == 2) result[kv[0]] = kv[1];
        }
        return result;
    }
}

// ── Excel Adapter ─────────────────────────────────────────────
// Uses ClosedXML (add NuGet: ClosedXML)

public sealed class ExcelAdapter : ISourceAdapter
{
    public DocumentSource SourceType => DocumentSource.Excel;
    private readonly ILogger<ExcelAdapter> _logger;

    public ExcelAdapter(ILogger<ExcelAdapter> logger) => _logger = logger;

    public async Task<RawDocument?> FetchAsync(
        string sourceRef, Dictionary<string, string>? meta, CancellationToken ct = default)
    {
        if (!File.Exists(sourceRef)) return null;

        // In production: ClosedXML.Excel workbook parsing
        // var wb = new XLWorkbook(sourceRef);
        // For blueprint: read as CSV fallback
        var content = await File.ReadAllTextAsync(sourceRef, ct);
        var hash    = RawDocument.ComputeHash(content);

        return new RawDocument
        {
            SourceRef    = sourceRef,
            Title        = Path.GetFileName(sourceRef),
            Content      = content,
            MimeType     = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            SizeBytes    = new FileInfo(sourceRef).Length,
            ContentHash  = hash,
            LastModified = File.GetLastWriteTimeUtc(sourceRef),
            Metadata     = new Dictionary<string, string> { ["path"] = sourceRef }
        };
    }

    public async IAsyncEnumerable<RawDocument> EnumerateAsync(
        string rootRef, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var dir = new DirectoryInfo(rootRef);
        if (!dir.Exists) yield break;

        foreach (var file in dir.GetFiles("*.xlsx").Concat(dir.GetFiles("*.xls")))
        {
            var doc = await FetchAsync(file.FullName, null, ct);
            if (doc != null) yield return doc;
        }
    }
}

// ── Custom API Adapter ────────────────────────────────────────

public sealed class CustomApiAdapter : ISourceAdapter
{
    public DocumentSource SourceType => DocumentSource.CustomApi;

    private readonly HttpClient _http;
    private readonly string     _apiBase;
    private readonly string?    _apiKey;
    private readonly ILogger<CustomApiAdapter> _logger;

    public CustomApiAdapter(HttpClient http, string apiBase, string? apiKey,
        ILogger<CustomApiAdapter> logger)
    {
        _http    = http;
        _apiBase = apiBase;
        _apiKey  = apiKey;
        _logger  = logger;
    }

    public async Task<RawDocument?> FetchAsync(
        string sourceRef, Dictionary<string, string>? meta, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/{sourceRef}");
        if (_apiKey != null) req.Headers.Add("X-Api-Key", _apiKey);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var content = await resp.Content.ReadAsStringAsync(ct);
        return new RawDocument
        {
            SourceRef    = sourceRef,
            Title        = sourceRef,
            Content      = content,
            MimeType     = resp.Content.Headers.ContentType?.MediaType ?? "text/plain",
            SizeBytes    = content.Length,
            ContentHash  = RawDocument.ComputeHash(content),
            LastModified = DateTime.UtcNow
        };
    }

    public async IAsyncEnumerable<RawDocument> EnumerateAsync(
        string rootRef, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/{rootRef}");
        if (_apiKey != null) req.Headers.Add("X-Api-Key", _apiKey);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) yield break;

        var json    = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var items   = json.RootElement.ValueKind == JsonValueKind.Array
                        ? json.RootElement
                        : json.RootElement.GetProperty("items");

        foreach (var item in items.EnumerateArray())
        {
            var id      = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : Guid.NewGuid().ToString();
            var content = item.GetRawText();
            yield return new RawDocument
            {
                SourceRef    = id,
                Title        = id,
                Content      = content,
                ContentHash  = RawDocument.ComputeHash(content),
                LastModified = DateTime.UtcNow
            };
        }
    }
}
