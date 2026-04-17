using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SharePointRag.Core.Connectors;

/// <summary>
/// Connector for Deltek Vantagepoint (and Costpoint) REST API.
/// Ingests Projects, Employees, Clients, and Opportunities as searchable records.
///
/// Required properties:
///   BaseUrl   – Deltek Vantagepoint API base URL, e.g. "https://api.vantagepoint.com/v1"
///   ApiKey    – Bearer token or API key
///   Entities  – comma-separated entity types: Projects, Employees, Clients, Opportunities
///
/// Optional properties:
///   Filter    – OData filter applied to all requests, e.g. "Status eq 'Active'"
///   PageSize  – records per page (default 100)
///
/// Example appsettings entry:
/// {
///   "Name": "DeltekProjects",
///   "Type": "Deltek",
///   "Properties": {
///     "BaseUrl":  "https://yourfirm.deltekfirst.com/VantagePoint/api/v1",
///     "ApiKey":   "Bearer eyJhbGci...",
///     "Entities": "Projects,Clients",
///     "Filter":   "ProjectStatus eq 'Active'",
///     "PageSize": "200"
///   }
/// }
/// </summary>
public sealed class DeltekConnector : IDataSourceConnector
{
    private readonly DataSourceDefinition _def;
    private readonly ILogger              _logger;
    private readonly HttpClient           _http;

    public string DataSourceName => _def.Name;
    public DataSourceType ConnectorType => DataSourceType.Deltek;

    private string BaseUrl   => _def.Get(DeltekProps.BaseUrl).TrimEnd('/');
    private string ApiKey    => _def.Get(DeltekProps.ApiKey);
    private int    PageSize  => _def.GetInt(DeltekProps.PageSize, 100);
    private string Filter    => _def.Get(DeltekProps.Filter);
    private IEnumerable<string> Entities =>
        _def.Get(DeltekProps.Entities, "Projects")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public DeltekConnector(DataSourceDefinition def, ILogger logger)
    {
        _def    = def;
        _logger = logger;
        _http   = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            ApiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? AuthenticationHeaderValue.Parse(ApiKey)
                : new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public async IAsyncEnumerable<SourceRecord> GetRecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entity in Entities)
            await foreach (var r in FetchEntityAsync(entity, null, ct))
                yield return r;
    }

    public async IAsyncEnumerable<SourceRecord> GetModifiedRecordsAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entity in Entities)
            await foreach (var r in FetchEntityAsync(entity, since, ct))
                yield return r;
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/ping", ct);
            return resp.IsSuccessStatusCode
                ? $"Connected to Deltek API at {BaseUrl}"
                : $"Deltek API returned {resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    private async IAsyncEnumerable<SourceRecord> FetchEntityAsync(
        string entity, DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int skip = 0;
        bool hasMore = true;

        while (hasMore)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildUrl(entity, since, skip);
            _logger.LogDebug("[{Src}] Fetching {Url}", _def.Name, url);

            JsonDocument? doc;
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                doc = JsonDocument.Parse(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Src}] Failed to fetch {Entity}", _def.Name, entity);
                yield break;
            }

            var root = doc.RootElement;

            // Try common Deltek OData response shapes
            JsonElement items = root.TryGetProperty("value",   out var v) ? v :
                                root.TryGetProperty("results", out var r) ? r :
                                root.TryGetProperty("data",    out var d) ? d : root;

            if (items.ValueKind != JsonValueKind.Array) { hasMore = false; continue; }

            int count = 0;
            foreach (var item in items.EnumerateArray())
            {
                var record = MapEntityToRecord(entity, item);
                if (record is not null) yield return record;
                count++;
            }

            skip    += count;
            hasMore  = count == PageSize;
            doc.Dispose();
        }
    }

    private string BuildUrl(string entity, DateTimeOffset? since, int skip)
    {
        var filters = new List<string>();
        if (!string.IsNullOrEmpty(Filter))  filters.Add(Filter);
        if (since.HasValue)                 filters.Add(BuildDeltaFilter(entity, since.Value));

        var filterQs = filters.Count > 0
            ? $"&$filter={Uri.EscapeDataString(string.Join(" and ", filters))}"
            : string.Empty;

        return $"{BaseUrl}/{entity}?$top={PageSize}&$skip={skip}{filterQs}";
    }

    private static string BuildDeltaFilter(string entity, DateTimeOffset since) => entity switch
    {
        "Projects"      => $"LastModified gt {since:O}",
        "Employees"     => $"LastUpdatedDate gt {since:O}",
        "Clients"       => $"LastModifiedDate gt {since:O}",
        "Opportunities" => $"ModifiedDate gt {since:O}",
        _               => $"LastModified gt {since:O}"
    };

    private SourceRecord? MapEntityToRecord(string entity, JsonElement item)
    {
        string Get(params string[] keys)
        {
            foreach (var k in keys)
                if (item.TryGetProperty(k, out var p)) return p.GetString() ?? string.Empty;
            return string.Empty;
        }

        return entity switch
        {
            "Projects" => new SourceRecord
            {
                Id             = $"{_def.Name}::Projects::{Get("ProjectNumber","Id")}",
                Title          = Get("ProjectName", "Name"),
                Content        = BuildProjectContent(item),
                Url            = Get("ProjectUrl", "Url"),
                Author         = Get("ProjectManager", "ManagerName"),
                LastModified   = ParseDate(Get("LastModified")),
                DataSourceName = _def.Name,
                Metadata       = BuildMetadata(item, entity)
            },
            "Employees" => new SourceRecord
            {
                Id             = $"{_def.Name}::Employees::{Get("EmployeeNumber","Id")}",
                Title          = $"{Get("FirstName")} {Get("LastName")}".Trim(),
                Content        = BuildEmployeeContent(item),
                Url            = Get("ProfileUrl", "Url"),
                DataSourceName = _def.Name,
                Metadata       = BuildMetadata(item, entity)
            },
            "Clients" => new SourceRecord
            {
                Id             = $"{_def.Name}::Clients::{Get("ClientNumber","Id")}",
                Title          = Get("ClientName", "Name"),
                Content        = BuildClientContent(item),
                Url            = Get("Url"),
                DataSourceName = _def.Name,
                Metadata       = BuildMetadata(item, entity)
            },
            "Opportunities" => new SourceRecord
            {
                Id             = $"{_def.Name}::Opportunities::{Get("OpportunityNumber","Id")}",
                Title          = Get("OpportunityName", "Name"),
                Content        = BuildOpportunityContent(item),
                Url            = Get("Url"),
                DataSourceName = _def.Name,
                Metadata       = BuildMetadata(item, entity)
            },
            _ => null
        };
    }

    // ── Content builders — combine fields into rich searchable text ───────────

    private static string BuildProjectContent(JsonElement p)
    {
        var sb = new System.Text.StringBuilder();
        Append(sb, "Project",     p, "ProjectNumber");
        Append(sb, "Name",        p, "ProjectName");
        Append(sb, "Client",      p, "ClientName");
        Append(sb, "Description", p, "Description", "ProjectDescription");
        Append(sb, "Status",      p, "ProjectStatus", "Status");
        Append(sb, "Type",        p, "ProjectType");
        Append(sb, "Manager",     p, "ProjectManager");
        Append(sb, "Start",       p, "StartDate");
        Append(sb, "End",         p, "EndDate", "CompletionDate");
        Append(sb, "Budget",      p, "Budget", "ContractAmount");
        Append(sb, "NAICS",       p, "NAICSCode");
        return sb.ToString();
    }

    private static string BuildEmployeeContent(JsonElement e)
    {
        var sb = new System.Text.StringBuilder();
        Append(sb, "Name",       e, "FirstName", "FullName");
        Append(sb, "Last",       e, "LastName");
        Append(sb, "Title",      e, "Title", "JobTitle");
        Append(sb, "Department", e, "Department");
        Append(sb, "Skills",     e, "Skills", "Expertise");
        Append(sb, "Clearance",  e, "SecurityClearance", "ClearanceLevel");
        Append(sb, "Education",  e, "Education");
        return sb.ToString();
    }

    private static string BuildClientContent(JsonElement c)
    {
        var sb = new System.Text.StringBuilder();
        Append(sb, "Client",  c, "ClientName", "Name");
        Append(sb, "Agency",  c, "Agency", "AgencyName");
        Append(sb, "Contact", c, "PrimaryContact");
        Append(sb, "Address", c, "Address");
        return sb.ToString();
    }

    private static string BuildOpportunityContent(JsonElement o)
    {
        var sb = new System.Text.StringBuilder();
        Append(sb, "Opportunity", o, "OpportunityName", "Name");
        Append(sb, "Client",      o, "ClientName");
        Append(sb, "Description", o, "Description");
        Append(sb, "Stage",       o, "Stage", "PipelineStage");
        Append(sb, "Value",       o, "EstimatedValue");
        Append(sb, "NAICS",       o, "NAICSCode");
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, string label,
        JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var p) && p.ValueKind != JsonValueKind.Null)
            {
                sb.AppendLine($"{label}: {p}");
                return;
            }
    }

    private static Dictionary<string, string> BuildMetadata(JsonElement item, string entity)
    {
        var dict = new Dictionary<string, string> { ["EntityType"] = entity };
        foreach (var prop in item.EnumerateObject())
            dict[prop.Name] = prop.Value.ToString();
        return dict;
    }

    private static DateTimeOffset ParseDate(string s) =>
        DateTimeOffset.TryParse(s, out var dt) ? dt : DateTimeOffset.UtcNow;
}

public sealed class DeltekConnectorFactory(
    ILogger<DeltekConnector> logger) : IDataSourceConnectorFactory
{
    public bool CanCreate(DataSourceType type) => type == DataSourceType.Deltek;
    public IDataSourceConnector Create(DataSourceDefinition def) =>
        new DeltekConnector(def, logger);
}
