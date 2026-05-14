using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharePointRag.Core.Extensions;
using SharePointRag.Indexer;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json",                                optional: false)
           .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true)
           .AddEnvironmentVariables()
           .AddUserSecrets<Program>(optional: true)
           .AddCommandLine(args);
    })
    .ConfigureServices((ctx, services) =>
    {
        // Full RAG infrastructure (registry, crawlers, stores, pipelines)
        services.AddSharePointRag(ctx.Configuration);

        // Indexer scheduling options
        services.Configure<IndexerOptions>(ctx.Configuration.GetSection(IndexerOptions.SectionName));

        // Scheduled indexer worker
        services.AddHostedService<IndexerWorker>();
    })
    .Build();

await host.RunAsync();
