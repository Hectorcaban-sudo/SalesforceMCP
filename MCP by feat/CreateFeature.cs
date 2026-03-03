using MediatR;
using McpServer.Infrastructure.Salesforce;
using McpServer.Models;
using System.Text.Json;

namespace McpServer.Features.Create;

// ─── Command ────────────────────────────────────────────────────────────────

public class CreateCommand : IRequest<McpResult>
{
    public string? Input { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public Dictionary<string, object?>? Fields { get; set; }

    public static CreateCommand FromJson(JsonElement el)
    {
        Dictionary<string, object?>? fields = null;
        if (el.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in f.EnumerateObject())
                fields[prop.Name] = prop.Value.GetString();
        }

        return new CreateCommand
        {
            Input = el.TryGetProperty("input", out var i) ? i.GetString() : null,
            ObjectName = el.TryGetProperty("objectName", out var o) ? o.GetString() ?? "" : "",
            Fields = fields
        };
    }
}

// ─── Handler ────────────────────────────────────────────────────────────────

public class CreateHandler : IRequestHandler<CreateCommand, McpResult>
{
    private readonly ISalesforceClient _sf;
    private readonly INaturalLanguageParser _parser;
    private readonly SchemaRegistry _registry;
    private readonly ILogger<CreateHandler> _logger;

    public CreateHandler(ISalesforceClient sf, INaturalLanguageParser parser,
        SchemaRegistry registry, ILogger<CreateHandler> logger)
    {
        _sf = sf;
        _parser = parser;
        _registry = registry;
        _logger = logger;
    }

    public async Task<McpResult> Handle(CreateCommand cmd, CancellationToken ct)
    {
        try
        {
            var objectName = cmd.ObjectName;
            var schema = _registry.GetSchema(objectName);
            if (schema == null)
                return McpResult.Fail($"Unknown Salesforce object: '{objectName}'. Run salesforce_list_objects to see available objects.");

            // Merge fields from NL input + explicit fields dict
            var fieldValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(cmd.Input))
            {
                var intent = _parser.Parse(cmd.Input, objectName);
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
                return McpResult.Fail("No field values provided for create.");

            // Validate createable fields
            var invalidFields = fieldValues.Keys
                .Where(f => schema.Fields.Any(sf => sf.Name == f && !sf.Createable))
                .ToList();
            if (invalidFields.Count > 0)
                return McpResult.Fail($"Fields not createable: {string.Join(", ", invalidFields)}");

            var record = fieldValues.ToDictionary(k => k.Key, k => k.Value);
            var result = await _sf.CreateAsync(schema.Name, record);

            _logger.LogInformation("Created {Object} Id={Id}", schema.Name, result.id);
            return McpResult.Ok(new { id = result.id?.ToString(), objectName = schema.Name, success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Create failed");
            return McpResult.Fail(ex.Message);
        }
    }
}

// ─── Endpoint ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/[controller]")]
public class CreateController : ControllerBase
{
    private readonly IMediator _mediator;

    public CreateController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
