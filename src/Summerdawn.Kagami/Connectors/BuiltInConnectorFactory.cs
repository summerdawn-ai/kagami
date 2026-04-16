namespace Summerdawn.Kagami.Connectors;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors.Google;
using Summerdawn.Kagami.Connectors.Microsoft;

/// <summary>
/// Built-in connector factory for supported provider connectors.
/// </summary>
public sealed class BuiltInConnectorFactory(ILoggerFactory loggerFactory) : IConnectorFactory
{
    private readonly HttpClient httpClient = CreateHttpClient();
    private readonly UnsupportedConnectorFactory unsupportedFactory = new();

    /// <inheritdoc />
    public IConnector Create(string endpointName, EndpointOptions endpoint, IConnectorCredential credential)
    {
        return endpoint.Type switch
        {
            EndpointOptions.GoogleContacts => new GoogleContactsConnector(
                httpClient,
                endpointName,
                endpoint,
                credential as GoogleOAuthCredential
                    ?? throw new InvalidOperationException($"Endpoint '{endpointName}' requires a GoogleOAuthCredential."),
                loggerFactory.CreateLogger<GoogleContactsConnector>()),
            EndpointOptions.MicrosoftContacts => new MicrosoftContactsConnector(
                httpClient,
                endpointName,
                endpoint,
                credential as MicrosoftClientCredential
                    ?? throw new InvalidOperationException($"Endpoint '{endpointName}' requires a MicrosoftClientCredential."),
                loggerFactory.CreateLogger<MicrosoftContactsConnector>()),
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
