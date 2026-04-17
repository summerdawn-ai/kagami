
using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;

namespace Summerdawn.Kagami.Tests.TestDoubles;

public sealed class FakeConnectorFactory : IConnectorFactory
{
    private readonly Dictionary<string, IConnector> connectors = [];

    public void Register(string endpointName, IConnector connector) => connectors[endpointName] = connector;

    public IConnector Create(string endpointName, EndpointOptions endpoint, IConnectorCredential credential)
    {
        _ = endpoint;
        _ = credential;
        if (connectors.TryGetValue(endpointName, out var connector))
        {
            return connector;
        }

        var created = new FakeConnector();
        connectors[endpointName] = created;
        return created;
    }
}
