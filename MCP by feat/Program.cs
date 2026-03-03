using McpServer.Infrastructure.Mcp;
using McpServer.Infrastructure.Salesforce;
using McpServer.Features.Query;
using McpServer.Features.Create;
using McpServer.Features.Update;
using McpServer.Features.Delete;
using McpServer.Features.Export;
using McpServer.Features.Schema;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/mcpserver-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// MediatR - scans all feature handlers
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

// Infrastructure
builder.Services.AddSingleton<SchemaRegistry>();
builder.Services.AddScoped<ISalesforceClient, SalesforceClient>();
builder.Services.AddScoped<INaturalLanguageParser, NaturalLanguageParser>();

// MCP Tools registration
builder.Services.AddScoped<McpToolRegistry>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.MapControllers();

// MCP endpoints
app.MapMcpEndpoints();

app.Run();
