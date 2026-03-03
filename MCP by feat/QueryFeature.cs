using MediatR;
using McpServer.Infrastructure.Salesforce;
using McpServer.Models;
using System.Text.Json;

namespace McpServer.Features.Query;

// ─── Command ────────────────────────────────────────────────────────────────

public class QueryCommand : IRequest<McpResult>
{
    public string Input { get; set; } = string.Empty;
    public string? ObjectName { get; set; }
    public int? Limit { get; set; }

    public static QueryCommand FromJson(JsonElement el)
    {
        return new QueryCommand
        {
            Input = el.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "",
            ObjectName = el.TryGetProperty("objectName", out var o) ? o.GetString() : null,
            Limit = el.TryGetProperty("limit", out var l) ? l.GetInt32() : null
        };
    }
}

// ─── Handler ────────────────────────────────────────────────────────────────

public class QueryHandler : IRequestHandler<QueryCommand, McpResult>
{
    private readonly ISalesforceClient _sf;
    private readonly INaturalLanguageParser _parser;
    private readonly SchemaRegistry _registry;
    private readonly ILogger<QueryHandler> _logger;

    public QueryHandler(ISalesforceClient sf, INaturalLanguageParser parser,
        SchemaRegistry registry, ILogger<QueryHandler> logger)
    {
        _sf = sf;
        _parser = parser;
        _registry = registry;
        _logger = logger;
    }

    public async Task<McpResult> Handle(QueryCommand cmd, CancellationToken ct)
    {
        try
        {
            var intent = _parser.Parse(cmd.Input, cmd.ObjectName);

            // Override limit if explicitly provided in tool call
            if (cmd.Limit.HasValue)
                intent.Limit = cmd.Limit;

            var soql = intent.RawSoql ?? BuildSoql(intent);
            if (string.IsNullOrEmpty(soql))
                return McpResult.Fail($"Could not determine Salesforce object from: '{cmd.Input}'");

            _logger.LogInformation("Executing SOQL: {Soql}", soql);
            var result = await _sf.QueryAsync(soql);

            return McpResult.Ok(result.records, totalSize: (int)(result.totalSize ?? 0), done: (bool)(result.done ?? true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Query failed");
            return McpResult.Fail(ex.Message);
        }
    }

    private string BuildSoql(ParsedIntent intent)
    {
        if (string.IsNullOrEmpty(intent.ObjectName)) return string.Empty;

        var schema = _registry.GetSchema(intent.ObjectName);
        var fields = intent.Fields.Count > 0
            ? intent.Fields
            : schema != null ? _registry.GetDefaultFields(schema) : new List<string> { "Id", "Name" };

        var soql = $"SELECT {string.Join(", ", fields)} FROM {intent.ObjectName}";

        if (!string.IsNullOrEmpty(intent.WhereClause))
            soql += $" WHERE {intent.WhereClause}";

        if (!string.IsNullOrEmpty(intent.OrderBy))
            soql += $" ORDER BY {intent.OrderBy} {(intent.OrderDescending ? "DESC" : "ASC")}";

        var limit = Math.Min(intent.Limit ?? 50, 200);
        soql += $" LIMIT {limit}";

        return soql;
    }
}

// ─── Endpoint ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/[controller]")]
public class QueryController : ControllerBase
{
    private readonly IMediator _mediator;

    public QueryController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> Query([FromBody] QueryCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<IActionResult> QueryGet([FromQuery] string q, [FromQuery] string? obj, [FromQuery] int? limit)
    {
        var result = await _mediator.Send(new QueryCommand { Input = q, ObjectName = obj, Limit = limit });
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
