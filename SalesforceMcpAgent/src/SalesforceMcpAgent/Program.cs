using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.ClientModel;
using SalesforceMcpAgent;

// ============================================================================
//  Host & Configuration
// ============================================================================

var host = Host.CreateApplicationBuilder(args);

host.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.local.json", optional: true,  reloadOnChange: false)
    .AddEnvironmentVariables();

host.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);  // flip to Debug to see MCP traffic

host.Services
    .AddOptions<SalesforceOptions>()
    .Bind(host.Configuration.GetSection(SalesforceOptions.Section))
    .ValidateOnStart();

host.Services
    .AddOptions<AzureOpenAIOptions>()
    .Bind(host.Configuration.GetSection(AzureOpenAIOptions.Section))
    .ValidateOnStart();

var app = host.Build();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var logger        = loggerFactory.CreateLogger("Program");

// ============================================================================
//  Read options
// ============================================================================

var sfOptions = host.Configuration
    .GetSection(SalesforceOptions.Section)
    .Get<SalesforceOptions>()
    ?? throw new InvalidOperationException("Salesforce configuration section is missing.");

var aoaiOptions = host.Configuration
    .GetSection(AzureOpenAIOptions.Section)
    .Get<AzureOpenAIOptions>()
    ?? throw new InvalidOperationException("AzureOpenAI configuration section is missing.");

// ============================================================================
//  Build the Azure OpenAI chat client
// ============================================================================

IChatClient chatClient;

if (!string.IsNullOrWhiteSpace(aoaiOptions.ApiKey))
{
    chatClient = new AzureOpenAIClient(
            new Uri(aoaiOptions.Endpoint),
            new ApiKeyCredential(aoaiOptions.ApiKey))
        .GetChatClient(aoaiOptions.DeploymentName)
        .AsIChatClient();
}
else
{
    // Managed Identity / Azure CLI credential – ideal for production.
    chatClient = new AzureOpenAIClient(
            new Uri(aoaiOptions.Endpoint),
            new DefaultAzureCredential())
        .GetChatClient(aoaiOptions.DeploymentName)
        .AsIChatClient();
}

// ============================================================================
//  Connect to Salesforce Hosted MCP Server
// ============================================================================

var tokenProvider = new SalesforceTokenProvider(sfOptions);

logger.LogInformation("Connecting to Salesforce Hosted MCP Server at {Url}…", sfOptions.McpServerUrl);

await using var mcpClient = await SalesforceMcpClientFactory.CreateAsync(
    sfOptions, tokenProvider, loggerFactory);

// ============================================================================
//  Create the agent
// ============================================================================

var agent = await SalesforceCrmAgent.CreateAsync(chatClient, mcpClient, loggerFactory);

// ============================================================================
//  Interactive REPL
// ============================================================================

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║       Salesforce CRM Agent  (type 'exit' to quit)   ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("You: ");
    Console.ResetColor();

    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    try
    {
        await agent.ChatStreamingAsync(input);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }

    Console.WriteLine();
}

Console.WriteLine("Goodbye!");
