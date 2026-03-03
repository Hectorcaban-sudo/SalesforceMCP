using McpServer.Features.Query;
using McpServer.Features.Create;
using McpServer.Features.Update;
using McpServer.Features.Delete;
using McpServer.Features.Export;
using McpServer.Features.Schema;
using MediatR;
using System.Text.Json;

namespace McpServer.Infrastructure.Mcp;

/// <summary>
/// Defines all MCP tools exposed by this server.
/// Each tool maps to a MediatR command/query in the vertical slice features.
/// </summary>
public class McpToolRegistry
{
    private readonly IMediator _mediator;
    private readonly ILogger<McpToolRegistry> _logger;

    public McpToolRegistry(IMediator mediator, ILogger<McpToolRegistry> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public static IEnumerable<McpToolDefinition> GetToolDefinitions() => new[]
    {
        new McpToolDefinition
        {
            Name = "salesforce_query",
            Description = "Query Salesforce records using natural language or SOQL. " +
                          "Examples: 'get all accounts', 'find contacts where email contains @gmail.com', " +
                          "'show top 10 opportunities ordered by amount desc', 'SELECT Id, Name FROM Lead LIMIT 5'",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    input = new { type = "string", description = "Natural language query or SOQL statement" },
                    objectName = new { type = "string", description = "Optional: Salesforce object API name (e.g., Account, Contact)" },
                    limit = new { type = "integer", description = "Max records to return (default 50, max 200)" }
                },
                required = new[] { "input" }
            }
        },
        new McpToolDefinition
        {
            Name = "salesforce_create",
            Description = "Create a new Salesforce record. " +
                          "Example: 'create account with Name=Acme Corp, Phone=555-1234'",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    input = new { type = "string", description = "Natural language description of the record to create" },
                    objectName = new { type = "string", description = "Salesforce object API name" },
                    fields = new
                    {
                        type = "object",
                        description = "Field name/value pairs for the record",
                        additionalProperties = new { type = "string" }
                    }
                },
                required = new[] { "objectName" }
            }
        },
        new McpToolDefinition
        {
            Name = "salesforce_update",
            Description = "Update an existing Salesforce record by ID. " +
                          "Example: 'update account 0015000000XXXXX set Phone=555-9999'",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    input = new { type = "string", description = "Natural language update instruction" },
                    objectName = new { type = "string", description = "Salesforce object API name" },
                    recordId = new { type = "string", description = "18-character Salesforce record ID" },
                    fields = new
                    {
                        type = "object",
                        description = "Field name/value pairs to update",
                        additionalProperties = new { type = "string" }
                    }
                },
                required = new[] { "recordId", "objectName" }
            }
        },
        new McpToolDefinition
        {
            Name = "salesforce_delete",
            Description = "Delete a Salesforce record by ID. CAUTION: This is permanent.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    objectName = new { type = "string", description = "Salesforce object API name" },
                    recordId = new { type = "string", description = "18-character Salesforce record ID" },
                    confirm = new { type = "boolean", description = "Must be true to confirm deletion" }
                },
                required = new[] { "objectName", "recordId", "confirm" }
            }
        },
        new McpToolDefinition
        {
            Name = "salesforce_export_csv",
            Description = "Export Salesforce records to CSV. " +
                          "Example: 'export all accounts to csv', 'download contacts where State = CA'",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    input = new { type = "string", description = "Natural language query describing what to export" },
                    objectName = new { type = "string", description = "Salesforce object API name" },
                    fileName = new { type = "string", description = "Optional custom file name (without .csv)" }
                },
                required = new[] { "input" }
            }
        },
        new McpToolDefinition
        {
            Name = "salesforce_describe",
            Description = "Describe a Salesforce object schema - lists all available fields, types, and relationships.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    objectName = new { type = "string", description = "Salesforce object API name or label" }
                },
                required = new[] { "objectName" }
            }
        },
        new McpToolDefinition
        {
            Name = "salesforce_list_objects",
            Description = "List all available Salesforce objects that have schemas loaded.",
            InputSchema = new { type = "object", properties = new { } }
        }
    };

    public async Task<object> ExecuteToolAsync(string toolName, JsonElement arguments)
    {
        _logger.LogInformation("Executing MCP tool: {Tool}", toolName);

        try
        {
            return toolName switch
            {
                "salesforce_query" => await _mediator.Send(QueryCommand.FromJson(arguments)),
                "salesforce_create" => await _mediator.Send(CreateCommand.FromJson(arguments)),
                "salesforce_update" => await _mediator.Send(UpdateCommand.FromJson(arguments)),
                "salesforce_delete" => await _mediator.Send(DeleteCommand.FromJson(arguments)),
                "salesforce_export_csv" => await _mediator.Send(ExportCommand.FromJson(arguments)),
                "salesforce_describe" => await _mediator.Send(DescribeCommand.FromJson(arguments)),
                "salesforce_list_objects" => await _mediator.Send(new ListObjectsCommand()),
                _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing tool {Tool}", toolName);
            return new { error = ex.Message };
        }
    }
}

public class McpToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object? InputSchema { get; set; }
}

/// <summary>
/// Extension to map MCP protocol endpoints onto the ASP.NET Core pipeline
/// </summary>
public static class McpEndpointExtensions
{
    public static WebApplication MapMcpEndpoints(this WebApplication app)
    {
        // MCP Initialize
        app.MapPost("/mcp", async (HttpContext ctx, McpToolRegistry registry) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var request = JsonSerializer.Deserialize<JsonElement>(body);

            var method = request.GetProperty("method").GetString();
            var id = request.TryGetProperty("id", out var idEl) ? idEl : (JsonElement?)null;

            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new
                    {
                        name = app.Configuration["Mcp:ServerName"] ?? "salesforce-mcp",
                        version = app.Configuration["Mcp:ServerVersion"] ?? "1.0.0"
                    }
                },
                "tools/list" => new
                {
                    tools = McpToolRegistry.GetToolDefinitions().Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = t.InputSchema
                    })
                },
                "tools/call" => await HandleToolCallAsync(request, registry),
                _ => new { error = $"Unknown method: {method}" }
            };

            var response = new
            {
                jsonrpc = "2.0",
                id = id?.ToString(),
                result
            };

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(response));
        });

        return app;
    }

    private static async Task<object> HandleToolCallAsync(JsonElement request, McpToolRegistry registry)
    {
        var @params = request.GetProperty("params");
        var toolName = @params.GetProperty("name").GetString()!;
        var arguments = @params.TryGetProperty("arguments", out var args) ? args : default;

        var result = await registry.ExecuteToolAsync(toolName, arguments);

        return new
        {
            content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) }
            }
        };
    }
}
