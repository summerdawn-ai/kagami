namespace Summerdawn.Kagami.Connectors;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors.Google;
using Summerdawn.Kagami.Connectors.Graph;

/// <summary>
/// Built-in connector factory for supported provider connectors.
/// </summary>
public sealed class BuiltInConnectorFactory(ILoggerFactory loggerFactory) : IConnectorFactory
{
    private readonly HttpClient httpClient = CreateHttpClient();
    private readonly UnsupportedConnectorFactory unsupportedFactory = new();

    /// <inheritdoc />
    public IConnector Create(string endpointName, EndpointOptions endpoint, CredentialOptions? credential)
    {
        return endpoint.Type switch
        {
            "google-contacts" => new GoogleContactsConnector(
                httpClient,
                endpointName,
                endpoint,
                credential ?? throw new InvalidOperationException($"Endpoint '{endpointName}' requires credentials."),
                loggerFactory.CreateLogger<GoogleContactsConnector>()),
            "graph-contacts" => new GraphContactsConnector(
                httpClient,
                endpointName,
                endpoint,
                credential ?? throw new InvalidOperationException($"Endpoint '{endpointName}' requires credentials."),
                loggerFactory.CreateLogger<GraphContactsConnector>()),
            _ => unsupportedFactory.Create(endpointName, endpoint, credential),
        };
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");
        return client;
    }
}
