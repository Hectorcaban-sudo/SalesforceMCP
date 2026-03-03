using Salesforce.Common;
using Salesforce.Force;

namespace McpServer.Infrastructure.Salesforce;

public interface ISalesforceClient
{
    Task<dynamic> QueryAsync(string soql);
    Task<dynamic> CreateAsync(string objectName, object record);
    Task<bool> UpdateAsync(string objectName, string id, object record);
    Task<bool> DeleteAsync(string objectName, string id);
    Task<dynamic> GetByIdAsync(string objectName, string id, IEnumerable<string> fields);
}

public class SalesforceClient : ISalesforceClient, IDisposable
{
    private ForceClient? _client;
    private readonly IConfiguration _config;
    private readonly ILogger<SalesforceClient> _logger;
    private bool _initialized = false;

    public SalesforceClient(IConfiguration config, ILogger<SalesforceClient> logger)
    {
        _config = config;
        _logger = logger;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        var sf = _config.GetSection("Salesforce");
        var authClient = new AuthenticationClient();

        var loginUrl = sf.GetValue<bool>("IsSandbox")
            ? "https://test.salesforce.com"
            : "https://login.salesforce.com";

        await authClient.UsernamePasswordAsync(
            sf["ClientId"]!,
            sf["ClientSecret"]!,
            sf["Username"]!,
            sf["Password"]! + sf["SecurityToken"],
            loginUrl);

        _client = new ForceClient(
            authClient.InstanceUrl,
            authClient.AccessToken,
            authClient.ApiVersion);

        _initialized = true;
        _logger.LogInformation("Salesforce client initialized. Instance: {Url}", authClient.InstanceUrl);
    }

    public async Task<dynamic> QueryAsync(string soql)
    {
        await EnsureInitializedAsync();
        _logger.LogDebug("Executing SOQL: {Soql}", soql);
        return await _client!.QueryAsync<dynamic>(soql);
    }

    public async Task<dynamic> CreateAsync(string objectName, object record)
    {
        await EnsureInitializedAsync();
        _logger.LogDebug("Creating {Object}", objectName);
        return await _client!.CreateAsync(objectName, record);
    }

    public async Task<bool> UpdateAsync(string objectName, string id, object record)
    {
        await EnsureInitializedAsync();
        _logger.LogDebug("Updating {Object} Id={Id}", objectName, id);
        var result = await _client!.UpdateAsync(objectName, id, record);
        return result.Success;
    }

    public async Task<bool> DeleteAsync(string objectName, string id)
    {
        await EnsureInitializedAsync();
        _logger.LogDebug("Deleting {Object} Id={Id}", objectName, id);
        return await _client!.DeleteAsync(objectName, id);
    }

    public async Task<dynamic> GetByIdAsync(string objectName, string id, IEnumerable<string> fields)
    {
        await EnsureInitializedAsync();
        var fieldList = string.Join(",", fields);
        var soql = $"SELECT {fieldList} FROM {objectName} WHERE Id = '{id}' LIMIT 1";
        return await _client!.QueryAsync<dynamic>(soql);
    }

    public void Dispose() => _client?.Dispose();
}
