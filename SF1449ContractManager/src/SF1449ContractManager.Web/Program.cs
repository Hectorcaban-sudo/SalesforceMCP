using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using SF1449ContractManager.Core.Data;
using SF1449ContractManager.Core.Extraction;
using SF1449ContractManager.Core.Repositories;
using System.ClientModel;

var builder = WebApplication.CreateBuilder(args);

// --- Razor / Blazor -------------------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Persistence -----------------------------------------------------------
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
Directory.CreateDirectory(Path.Combine(builder.Environment.WebRootPath, "uploads"));

builder.Services.AddDbContext<ContractDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ContractDb")));

builder.Services.AddScoped<IContractRepository, ContractRepository>();

// --- AI chat client (Microsoft Agent Framework sits on top of this) --------
var aiProvider = builder.Configuration["AIProvider:Provider"] ?? "OpenAI";

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var config = builder.Configuration;

    if (string.Equals(aiProvider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase))
    {
        var endpoint = new Uri(config["AIProvider:AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AIProvider:AzureOpenAI:Endpoint is not configured."));
        var deployment = config["AIProvider:AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("AIProvider:AzureOpenAI:DeploymentName is not configured.");
        var useManagedIdentity = config.GetValue<bool>("AIProvider:AzureOpenAI:UseManagedIdentity");

        AzureOpenAIClient azureClient = useManagedIdentity
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(config["AIProvider:AzureOpenAI:ApiKey"] ?? string.Empty));

        return azureClient.GetChatClient(deployment).AsIChatClient();
    }

    // Default: plain OpenAI
    var apiKey = config["AIProvider:OpenAI:ApiKey"]
        ?? throw new InvalidOperationException("AIProvider:OpenAI:ApiKey is not configured (use dotnet user-secrets).");
    var model = config["AIProvider:OpenAI:Model"] ?? "gpt-4o";

    var openAiClient = new OpenAIClient(apiKey);
    return openAiClient.GetChatClient(model).AsIChatClient();
});

builder.Services.AddScoped<IContractExtractionAgent, Sf1449ExtractionAgent>();
builder.Services.AddScoped<PdfTextExtractor>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<SF1449ContractManager.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
