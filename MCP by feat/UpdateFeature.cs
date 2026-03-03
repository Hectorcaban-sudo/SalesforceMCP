using MediatR;
using McpServer.Infrastructure.Salesforce;
using McpServer.Models;
using System.Text.Json;

namespace McpServer.Features.Update;

// ─── Command ────────────────────────────────────────────────────────────────

public class UpdateCommand : IRequest<McpResult>
{
    public string? Input { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public Dictionary<string, object?>? Fields { get; set; }

    public static UpdateCommand FromJson(JsonElement el)
    {
        Dictionary<string, object?>? fields = null;
        if (el.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in f.EnumerateObject())
                fields[prop.Name] = prop.Value.GetString();
        }

        return new UpdateCommand
        {
            Input = el.TryGetProperty("input", out var i) ? i.GetString() : null,
            ObjectName = el.TryGetProperty("objectName", out var o) ? o.GetString() ?? "" : "",
            RecordId = el.TryGetProperty("recordId", out var r) ? r.GetString() ?? "" : "",
            Fields = fields
        };
    }
}

// ─── Handler ────────────────────────────────────────────────────────────────

public class UpdateHandler : IRequestHandler<UpdateCommand, McpResult>
{
    private readonly ISalesforceClient _sf;
    private readonly INaturalLanguageParser _parser;
    private readonly SchemaRegistry _registry;
    private readonly ILogger<UpdateHandler> _logger;

    public UpdateHandler(ISalesforceClient sf, INaturalLanguageParser parser,
        SchemaRegistry registry, ILogger<UpdateHandler> logger)
    {
        _sf = sf;
        _parser = parser;
        _registry = registry;
        _logger = logger;
    }

    public async Task<McpResult> Handle(UpdateCommand cmd, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(cmd.RecordId))
                return McpResult.Fail("RecordId is required for update.");

            var schema = _registry.GetSchema(cmd.ObjectName);
            if (schema == null)
                return McpResult.Fail($"Unknown Salesforce object: '{cmd.ObjectName}'");

            var fieldValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(cmd.Input))
            {
                var intent = _parser.Parse(cmd.Input, cmd.ObjectName);
                foreach (var kv in intent.FieldValues)
                    fieldValues[kv.Key] = kv.Value;
            }

            if (cmd.Fields != null)
            {
                foreach (var kv in cmd.Fields)
                {
                    var resolvedField = _registry.ResolveFieldName(schema, kv.Key) ?? kv.Key;
                    fieldValues[resolvedField] = kv.Value;
                }
            }

            if (fieldValues.Count == 0)
                return McpResult.Fail("No field values provided for update.");

            // Validate updateable fields
            var invalid = fieldValues.Keys
                .Where(f => schema.Fields.Any(sf => sf.Name == f && !sf.Updateable))
                .ToList();
            if (invalid.Count > 0)
                return McpResult.Fail($"Fields not updateable: {string.Join(", ", invalid)}");

            var success = await _sf.UpdateAsync(schema.Name, cmd.RecordId, fieldValues);

            _logger.LogInformation("Updated {Object} Id={Id} Success={Ok}", schema.Name, cmd.RecordId, success);
            return McpResult.Ok(new { id = cmd.RecordId, objectName = schema.Name, success });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update failed");
            return McpResult.Fail(ex.Message);
        }
    }
}

// ─── Endpoint ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/[controller]")]
public class UpdateController : ControllerBase
{
    private readonly IMediator _mediator;

    public UpdateController(IMediator mediator) => _mediator = mediator;

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateCommand command)
    {
        command.RecordId = id;
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    public async Task<IActionResult> UpdatePost([FromBody] UpdateCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
