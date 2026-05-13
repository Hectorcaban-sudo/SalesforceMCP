using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using System.Runtime.CompilerServices;

namespace SharePointRag.Core.Connectors;

/// <summary>
/// Base class for custom data source connectors.
///
/// Extend this class to connect any proprietary system — CRM, ERP, REST API,
/// file server, blob storage, etc. — without touching any RAG infrastructure code.
///
/// Registration (in Program.cs or a DI extension):
///   services.AddCustomConnector&lt;MySystemConnector, MySystemConnectorFactory&gt;();
///
/// appsettings entry:
/// {
///   "Name":  "MyCRM",
///   "Type":  "Custom",
///   "Properties": {
///     "CustomType": "MyCompany.Connectors.MySystemConnector",  ← used by factory
///     ... your own keys ...
///   }
/// }
///
/// The CustomConnectorFactory resolves the concrete type by the "CustomType"
/// property value if your factory registers multiple Custom connectors.
/// </summary>
public abstract class CustomConnectorBase : IDataSourceConnector
{
    protected readonly DataSourceDefinition Definition;
    protected readonly ILogger Logger;

    public string DataSourceName => Definition.Name;
    public DataSourceType ConnectorType => DataSourceType.Custom;

    protected CustomConnectorBase(DataSourceDefinition definition, ILogger logger)
    {
        Definition = definition;
        Logger     = logger;
    }

    public abstract IAsyncEnumerable<SourceRecord> GetRecordsAsync(CancellationToken ct = default);

    public virtual IAsyncEnumerable<SourceRecord> GetModifiedRecordsAsync(
        DateTimeOffset since, CancellationToken ct = default) =>
        GetRecordsAsync(ct);   // default: no delta support — full re-ingest

    public virtual Task<string> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult($"Custom connector '{Definition.Name}' ready.");
}

/// <summary>
/// Default factory for Custom connectors.
/// Resolves the concrete connector type from the "CustomType" property,
/// which must be a fully-qualified type name resolvable via reflection.
///
/// For simpler setups, replace with a factory that switches on the Name property.
/// </summary>
public sealed class CustomConnectorFactory(
    IServiceProvider serviceProvider,
    ILogger<CustomConnectorFactory> logger) : IDataSourceConnectorFactory
{
    public bool CanCreate(DataSourceType type) => type == DataSourceType.Custom;

    public IDataSourceConnector Create(DataSourceDefinition def)
    {
        var typeName = def.Get("CustomType");
        if (string.IsNullOrEmpty(typeName))
            throw new InvalidOperationException(
                $"Custom connector '{def.Name}' must specify a 'CustomType' property " +
                "containing the fully-qualified C# type name of the connector implementation.");

        var type = Type.GetType(typeName)
                   ?? AppDomain.CurrentDomain.GetAssemblies()
                       .Select(a => a.GetType(typeName))
                       .FirstOrDefault(t => t != null)
                   ?? throw new InvalidOperationException(
                       $"Could not resolve connector type '{typeName}' for data source '{def.Name}'. " +
                       "Ensure the assembly is loaded and the type name is fully qualified.");

        if (!typeof(IDataSourceConnector).IsAssignableFrom(type))
            throw new InvalidOperationException(
                $"Type '{typeName}' does not implement IDataSourceConnector.");

        // Try to resolve from DI first, then fall back to reflection
        var connector = (IDataSourceConnector?)ActivatorUtilities
            .CreateInstance(serviceProvider, type, def);

        return connector
               ?? throw new InvalidOperationException($"Could not create connector of type '{typeName}'.");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  DI EXTENSION FOR CUSTOM CONNECTORS
// ═══════════════════════════════════════════════════════════════════════════════

public static class CustomConnectorExtensions
{
    /// <summary>
    /// Register a strongly-typed custom connector factory.
    ///
    /// Usage:
    ///   services.AddCustomConnector&lt;MyCrmConnector, MyCrmConnectorFactory&gt;();
    ///
    /// Then in appsettings, set "Type": "Custom" and any Properties your connector needs.
    /// Your factory's CanCreate() method can discriminate by checking def.Properties["CustomType"]
    /// or def.Name.
    /// </summary>
    public static IServiceCollection AddCustomConnector<TConnector, TFactory>(
        this IServiceCollection services)
        where TConnector : IDataSourceConnector
        where TFactory : class, IDataSourceConnectorFactory
    {
        services.AddSingleton<TFactory>();
        services.AddSingleton<IDataSourceConnectorFactory>(
            sp => sp.GetRequiredService<TFactory>());
        return services;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  EXAMPLE CUSTOM CONNECTOR — REST API skeleton
//  Copy and rename this class to implement your own source.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Skeleton for a custom REST API connector.
/// Set "CustomType": "SharePointRag.Core.Connectors.RestApiConnectorExample" in Properties.
///
/// Required properties: BaseUrl, ApiKey
/// Optional properties: Endpoint (default "/records"), PageSize (default 100)
/// </summary>
public sealed class RestApiConnectorExample : CustomConnectorBase
{
    private readonly HttpClient _http = new();

    public RestApiConnectorExample(DataSourceDefinition def, ILogger<RestApiConnectorExample> logger)
        : base(def, logger)
    {
        _http.BaseAddress = new Uri(def.Get("BaseUrl"));
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {def.Get("ApiKey")}");
    }

    public override async IAsyncEnumerable<SourceRecord> GetRecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoint = Definition.Get("Endpoint", "/records");
        var pageSize = Definition.GetInt("PageSize", 100);
        int page = 1;

        while (true)
        {
            var url = $"{endpoint}?page={page}&pageSize={pageSize}";
            Logger.LogDebug("[{Src}] GET {Url}", DataSourceName, url);

            var json = await _http.GetStringAsync(url, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var items = doc.RootElement.TryGetProperty("items", out var v) ? v : doc.RootElement;
            if (items.ValueKind != System.Text.Json.JsonValueKind.Array) yield break;

            int count = 0;
            foreach (var item in items.EnumerateArray())
            {
                yield return new SourceRecord
                {
                    Id             = item.TryGetProperty("id",      out var id) ? id.GetString()! : Guid.NewGuid().ToString(),
                    Title          = item.TryGetProperty("title",   out var t)  ? t.GetString()!  : "Unknown",
                    Content        = item.TryGetProperty("content", out var c)  ? c.GetString()!  : string.Empty,
                    Url            = item.TryGetProperty("url",     out var u)  ? u.GetString()!  : string.Empty,
                    DataSourceName = DataSourceName
                };
                count++;
            }

            if (count < pageSize) yield break;
            page++;
        }
    }

    public override async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("/health", ct);
            return $"REST API: {resp.StatusCode}";
        }
        catch (Exception ex) { return $"Failed: {ex.Message}"; }
    }
}
