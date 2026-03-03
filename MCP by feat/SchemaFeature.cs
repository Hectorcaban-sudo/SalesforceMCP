using MediatR;
using McpServer.Infrastructure.Salesforce;
using McpServer.Models;
using System.Text.Json;

namespace McpServer.Features.Schema;

// ─── Describe Command ────────────────────────────────────────────────────────

public class DescribeCommand : IRequest<McpResult>
{
    public string ObjectName { get; set; } = string.Empty;

    public static DescribeCommand FromJson(JsonElement el) => new()
    {
        ObjectName = el.TryGetProperty("objectName", out var o) ? o.GetString() ?? "" : ""
    };
}

public class DescribeHandler : IRequestHandler<DescribeCommand, McpResult>
{
    private readonly SchemaRegistry _registry;

    public DescribeHandler(SchemaRegistry registry) => _registry = registry;

    public Task<McpResult> Handle(DescribeCommand cmd, CancellationToken ct)
    {
        var schema = _registry.GetSchema(cmd.ObjectName);
        if (schema == null)
            return Task.FromResult(McpResult.Fail($"No schema found for '{cmd.ObjectName}'. Run salesforce_list_objects to see available objects."));

        var result = new
        {
            name = schema.Name,
            label = schema.Label,
            labelPlural = schema.LabelPlural,
            fieldCount = schema.Fields.Count,
            fields = schema.Fields.Select(f => new
            {
                name = f.Name,
                label = f.Label,
                type = f.Type,
                nillable = f.Nillable,
                createable = f.Createable,
                updateable = f.Updateable,
                filterable = f.Filterable,
                picklistValues = f.PicklistValues.Where(p => p.Active).Select(p => p.Value).ToList()
            }),
            relationships = schema.Relationships.Select(r => new
            {
                name = r.Name,
                referenceTo = r.ReferenceTo,
                relationshipName = r.RelationshipName
            })
        };

        return Task.FromResult(McpResult.Ok(result));
    }
}

// ─── List Objects Command ────────────────────────────────────────────────────

public class ListObjectsCommand : IRequest<McpResult> { }

public class ListObjectsHandler : IRequestHandler<ListObjectsCommand, McpResult>
{
    private readonly SchemaRegistry _registry;

    public ListObjectsHandler(SchemaRegistry registry) => _registry = registry;

    public Task<McpResult> Handle(ListObjectsCommand cmd, CancellationToken ct)
    {
        var objects = _registry.GetAllSchemas()
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                apiName = s.Name,
                label = s.Label,
                labelPlural = s.LabelPlural,
                fieldCount = s.Fields.Count
            })
            .ToList();

        return Task.FromResult(McpResult.Ok(new
        {
            count = objects.Count,
            objects
        }));
    }
}

// ─── Reload Schema Command ────────────────────────────────────────────────────

public class ReloadSchemaCommand : IRequest<McpResult> { }

public class ReloadSchemaHandler : IRequestHandler<ReloadSchemaCommand, McpResult>
{
    private readonly SchemaRegistry _registry;

    public ReloadSchemaHandler(SchemaRegistry registry) => _registry = registry;

    public Task<McpResult> Handle(ReloadSchemaCommand _, CancellationToken ct)
    {
        _registry.Reload();
        var count = _registry.GetAllSchemas().Count();
        return Task.FromResult(McpResult.Ok(new { reloaded = true, objectCount = count }));
    }
}

// ─── Endpoint ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/[controller]")]
public class SchemaController : ControllerBase
{
    private readonly IMediator _mediator;

    public SchemaController(IMediator mediator) => _mediator = mediator;

    [HttpGet("objects")]
    public async Task<IActionResult> ListObjects()
    {
        var result = await _mediator.Send(new ListObjectsCommand());
        return Ok(result);
    }

    [HttpGet("objects/{objectName}")]
    public async Task<IActionResult> Describe(string objectName)
    {
        var result = await _mediator.Send(new DescribeCommand { ObjectName = objectName });
        return result.Success ? Ok(result) : NotFound(result);
    }

    [HttpPost("reload")]
    public async Task<IActionResult> Reload()
    {
        var result = await _mediator.Send(new ReloadSchemaCommand());
        return Ok(result);
    }
}
