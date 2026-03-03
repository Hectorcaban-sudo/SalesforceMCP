using CsvHelper;
using CsvHelper.Configuration;
using MediatR;
using McpServer.Infrastructure.Salesforce;
using McpServer.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace McpServer.Features.Export;

// ─── Command ────────────────────────────────────────────────────────────────

public class ExportCommand : IRequest<ExportResult>
{
    public string Input { get; set; } = string.Empty;
    public string? ObjectName { get; set; }
    public string? FileName { get; set; }

    public static ExportCommand FromJson(JsonElement el) => new()
    {
        Input = el.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "",
        ObjectName = el.TryGetProperty("objectName", out var o) ? o.GetString() : null,
        FileName = el.TryGetProperty("fileName", out var f) ? f.GetString() : null
    };
}

public class ExportResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? FileName { get; set; }
    public int RecordCount { get; set; }
    public string? CsvContent { get; set; }  // Base64 encoded for MCP, raw for HTTP
    public string? Message { get; set; }

    public static ExportResult Ok(string fileName, int count, string csv) =>
        new() { Success = true, FileName = fileName, RecordCount = count, CsvContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv)),
                Message = $"Exported {count} records to {fileName}" };

    public static ExportResult Fail(string error) => new() { Success = false, Error = error };
}

// ─── Handler ────────────────────────────────────────────────────────────────

public class ExportHandler : IRequestHandler<ExportCommand, ExportResult>
{
    private readonly ISalesforceClient _sf;
    private readonly INaturalLanguageParser _parser;
    private readonly SchemaRegistry _registry;
    private readonly ILogger<ExportHandler> _logger;

    public ExportHandler(ISalesforceClient sf, INaturalLanguageParser parser,
        SchemaRegistry registry, ILogger<ExportHandler> logger)
    {
        _sf = sf;
        _parser = parser;
        _registry = registry;
        _logger = logger;
    }

    public async Task<ExportResult> Handle(ExportCommand cmd, CancellationToken ct)
    {
        try
        {
            var intent = _parser.Parse(cmd.Input, cmd.ObjectName);

            if (string.IsNullOrEmpty(intent.ObjectName))
                return ExportResult.Fail($"Could not determine Salesforce object from: '{cmd.Input}'");

            var schema = _registry.GetSchema(intent.ObjectName);
            var fields = intent.Fields.Count > 0
                ? intent.Fields
                : schema?.Fields.Select(f => f.Name).Take(30).ToList()
                  ?? new List<string> { "Id", "Name" };

            // Bump limit for exports - default 2000, max 50000
            intent.Limit ??= 2000;
            intent.Limit = Math.Min(intent.Limit.Value, 50000);

            var soql = intent.RawSoql ?? BuildSoql(intent, fields);
            _logger.LogInformation("Export SOQL: {Soql}", soql);

            var result = await _sf.QueryAsync(soql);
            var records = result.records as IEnumerable<dynamic> ?? Array.Empty<dynamic>();
            var recordList = records.Cast<object>().ToList();

            var csv = BuildCsv(recordList, fields);

            var fileName = string.IsNullOrEmpty(cmd.FileName)
                ? $"{intent.ObjectName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv"
                : $"{cmd.FileName}.csv";

            _logger.LogInformation("Exported {Count} {Object} records", recordList.Count, intent.ObjectName);
            return ExportResult.Ok(fileName, recordList.Count, csv);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            return ExportResult.Fail(ex.Message);
        }
    }

    private string BuildSoql(ParsedIntent intent, List<string> fields)
    {
        var soql = $"SELECT {string.Join(", ", fields)} FROM {intent.ObjectName}";
        if (!string.IsNullOrEmpty(intent.WhereClause))
            soql += $" WHERE {intent.WhereClause}";
        if (!string.IsNullOrEmpty(intent.OrderBy))
            soql += $" ORDER BY {intent.OrderBy} {(intent.OrderDescending ? "DESC" : "ASC")}";
        soql += $" LIMIT {intent.Limit}";
        return soql;
    }

    private string BuildCsv(List<object> records, List<string> fields)
    {
        if (records.Count == 0) return string.Join(",", fields) + "\n";

        var sb = new StringBuilder();

        // Dynamically discover all keys from first record
        var allKeys = new List<string>();
        if (records[0] is JObject firstJObj)
            allKeys = firstJObj.Properties()
                .Select(p => p.Name)
                .Where(n => n != "attributes")
                .ToList();
        else
            allKeys = fields;

        // Header row
        sb.AppendLine(string.Join(",", allKeys.Select(EscapeCsvField)));

        // Data rows
        foreach (var record in records)
        {
            var values = new List<string>();
            if (record is JObject jObj)
            {
                foreach (var key in allKeys)
                    values.Add(EscapeCsvField(jObj[key]?.ToString() ?? ""));
            }
            else
            {
                var dynRecord = record as dynamic;
                foreach (var key in allKeys)
                {
                    try { values.Add(EscapeCsvField((string?)((dynamic)record)?[key]?.ToString() ?? "")); }
                    catch { values.Add(""); }
                }
            }
            sb.AppendLine(string.Join(",", values));
        }

        return sb.ToString();
    }

    private static string EscapeCsvField(string? value)
    {
        if (value == null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

// ─── Endpoint ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/[controller]")]
public class ExportController : ControllerBase
{
    private readonly IMediator _mediator;

    public ExportController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Returns CSV file as a download
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportCsv([FromQuery] string q, [FromQuery] string? obj, [FromQuery] string? fileName)
    {
        var result = await _mediator.Send(new ExportCommand { Input = q, ObjectName = obj, FileName = fileName });
        if (!result.Success)
            return BadRequest(result);

        var csvBytes = Convert.FromBase64String(result.CsvContent!);
        return File(csvBytes, "text/csv", result.FileName);
    }

    [HttpPost]
    public async Task<IActionResult> ExportPost([FromBody] ExportCommand command)
    {
        var result = await _mediator.Send(command);
        if (!result.Success)
            return BadRequest(result);

        var csvBytes = Convert.FromBase64String(result.CsvContent!);
        return File(csvBytes, "text/csv", result.FileName);
    }
}
