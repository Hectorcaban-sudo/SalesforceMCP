using MediatR;
using McpServer.Infrastructure.Salesforce;
using McpServer.Models;
using System.Text.Json;

namespace McpServer.Features.Delete;

// ─── Command ────────────────────────────────────────────────────────────────

public class DeleteCommand : IRequest<McpResult>
{
    public string ObjectName { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public bool Confirm { get; set; }

    public static DeleteCommand FromJson(JsonElement el) => new()
    {
        ObjectName = el.TryGetProperty("objectName", out var o) ? o.GetString() ?? "" : "",
        RecordId = el.TryGetProperty("recordId", out var r) ? r.GetString() ?? "" : "",
        Confirm = el.TryGetProperty("confirm", out var c) && c.GetBoolean()
    };
}

// ─── Handler ────────────────────────────────────────────────────────────────

public class DeleteHandler : IRequestHandler<DeleteCommand, McpResult>
{
    private readonly ISalesforceClient _sf;
    private readonly SchemaRegistry _registry;
    private readonly ILogger<DeleteHandler> _logger;

    public DeleteHandler(ISalesforceClient sf, SchemaRegistry registry, ILogger<DeleteHandler> logger)
    {
        _sf = sf;
        _registry = registry;
        _logger = logger;
    }

    public async Task<McpResult> Handle(DeleteCommand cmd, CancellationToken ct)
    {
        try
        {
            if (!cmd.Confirm)
                return McpResult.Fail("Deletion requires 'confirm: true' to prevent accidental deletes.");

            if (string.IsNullOrEmpty(cmd.RecordId))
                return McpResult.Fail("RecordId is required.");

            var schema = _registry.GetSchema(cmd.ObjectName);
            if (schema == null)
                return McpResult.Fail($"Unknown Salesforce object: '{cmd.ObjectName}'");

            var success = await _sf.DeleteAsync(schema.Name, cmd.RecordId);

            _logger.LogWarning("Deleted {Object} Id={Id}", schema.Name, cmd.RecordId);
            return McpResult.Ok(new { id = cmd.RecordId, objectName = schema.Name, deleted = success });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete failed");
            return McpResult.Fail(ex.Message);
        }
    }
}

// ─── Endpoint ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/[controller]")]
public class DeleteController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeleteController(IMediator mediator) => _mediator = mediator;

    [HttpDelete("{objectName}/{id}")]
    public async Task<IActionResult> Delete(string objectName, string id, [FromQuery] bool confirm = false)
    {
        var result = await _mediator.Send(new DeleteCommand
        {
            ObjectName = objectName,
            RecordId = id,
            Confirm = confirm
        });
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    public async Task<IActionResult> DeletePost([FromBody] DeleteCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
