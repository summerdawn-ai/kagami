namespace Summerdawn.Kagami.Connectors;

using Summerdawn.Kagami.Configuration;

/// <summary>
/// Factory that creates <see cref="IConnector"/> instances for named endpoints.
/// </summary>
public interface IConnectorFactory
{
    /// <summary>
    /// Creates a connector for the given endpoint configuration.
    /// </summary>
    public IConnector Create(string endpointName, EndpointOptions endpoint, CredentialOptions? credential);
}
