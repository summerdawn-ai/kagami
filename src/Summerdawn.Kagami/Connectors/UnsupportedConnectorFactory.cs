namespace Summerdawn.Kagami.Connectors;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;

/// <summary>
/// Default connector factory used until real provider connectors are registered.
/// </summary>
public sealed class UnsupportedConnectorFactory : IConnectorFactory
{
    /// <inheritdoc />
    public IConnector Create(string endpointName, EndpointOptions endpoint, IConnectorCredential credential)
    {
        _ = credential;
        throw new InvalidOperationException(
            $"No connector factory is registered for endpoint '{endpointName}' of type '{endpoint.Type}'.");
    }
}
