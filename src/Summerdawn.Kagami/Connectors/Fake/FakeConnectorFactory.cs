namespace Summerdawn.Kagami.Connectors.Fake;

using Summerdawn.Kagami.Configuration;

/// <summary>
/// Connector factory that creates <see cref="FakeConnector"/> instances for testing.
/// </summary>
public sealed class FakeConnectorFactory : IConnectorFactory
{
    private readonly Dictionary<string, FakeConnector> connectors = [];

    /// <summary>
    /// Registers a specific <see cref="FakeConnector"/> for a named endpoint.
    /// </summary>
    public void Register(string endpointName, FakeConnector connector) =>
        connectors[endpointName] = connector;

    /// <inheritdoc/>
    public IConnector Create(string endpointName, EndpointOptions endpoint, CredentialOptions? credential)
    {
        if (connectors.TryGetValue(endpointName, out var connector))
        {
            return connector;
        }

        // Return a new empty fake connector if no specific one is registered
        var newConnector = new FakeConnector();
        connectors[endpointName] = newConnector;
        return newConnector;
    }
}
