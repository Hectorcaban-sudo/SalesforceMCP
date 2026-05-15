using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Core.Connectors;

/// <summary>
/// Central registry of all IDataSourceConnectorFactory implementations.
///
/// At startup the DI extension registers built-in factories (SharePoint, SQL, Excel, Deltek).
/// Third-party or custom connectors can call Register() to add themselves.
///
/// Resolution order: factories registered LAST win (allows overriding built-ins).
/// </summary>
public sealed class ConnectorRegistry : IConnectorRegistry
{
    private readonly List<IDataSourceConnectorFactory> _factories = [];

    public void Register(IDataSourceConnectorFactory factory) =>
        _factories.Add(factory);

    public IDataSourceConnector Resolve(DataSourceDefinition definition)
    {
        // Search in reverse so last-registered factory wins
        for (int i = _factories.Count - 1; i >= 0; i--)
        {
            if (_factories[i].CanCreate(definition.Type))
                return _factories[i].Create(definition);
        }

        throw new InvalidOperationException(
            $"No connector factory registered for DataSourceType '{definition.Type}'. " +
            $"Data source: '{definition.Name}'. " +
            $"Register a factory via IConnectorRegistry.Register() or services.AddCustomConnector().");
    }
}
