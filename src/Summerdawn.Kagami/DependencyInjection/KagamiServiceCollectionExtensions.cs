
using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using Polly;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Connectors.Google;
using Summerdawn.Kagami.Connectors.Microsoft;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.DependencyInjection;
/// <summary>
/// Extension methods for registering Kagami services.
/// </summary>
public static class KagamiServiceCollectionExtensions
{
    private static readonly string DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Summerdawn.ai", "Kagami", "kagami-state.db");

    private static readonly string DatabaseDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Summerdawn.ai", "Kagami");

    /// <summary>Adds Kagami core services to the DI container.</summary>
    public static IServiceCollection AddKagami(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new KagamiOptions();
        configuration.Bind(options);
        services.AddSingleton(options);

        // Build a shared HttpClient for credential/token operations (not API calls)
        HttpClient authHttpClient = new();
        authHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");

        // Pre-build one credential per endpoint at startup
        var credentials = new Dictionary<string, IConnectorCredential>(StringComparer.OrdinalIgnoreCase);
        foreach (var (endpointName, endpoint) in options.Endpoints)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.Credential.Type))
            {
                var scopes = ScopesFor(endpoint.Type);
                credentials[endpointName] = CredentialFactory.Create(endpoint.Credential, authHttpClient, scopes, endpointName);
            }
        }

        // Register a named HttpClient and a keyed IConnector singleton per endpoint
        services.AddHttpClient();
        foreach (var (endpointName, endpoint) in options.Endpoints)
        {
            RegisterEndpointHttpClient(services, endpointName, endpoint.Type);

            string capturedEndpointName = endpointName;
            var capturedEndpoint = endpoint;
            services.AddKeyedSingleton<IConnector>(endpointName, (sp, _) =>
            {
                var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient($"kagami-{capturedEndpointName}");
                credentials.TryGetValue(capturedEndpointName, out var credential);
                return capturedEndpoint.Type switch
                {
                    EndpointOptions.GoogleContacts => new GoogleContactsConnector(
                        httpClient,
                        capturedEndpointName,
                        capturedEndpoint,
                        credential as GoogleOAuthCredential
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a GoogleOAuthCredential."),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GoogleContactsConnector>()),
                    EndpointOptions.MicrosoftContacts => new MicrosoftContactsConnector(
                        httpClient,
                        capturedEndpointName,
                        capturedEndpoint,
                        credential as MicrosoftClientCredential
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a MicrosoftClientCredential."),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MicrosoftContactsConnector>()),
                    _ => throw new InvalidOperationException(
                        $"No connector registered for endpoint '{capturedEndpointName}' of type '{capturedEndpoint.Type}'."),
                };
            });
        }

        // Func<string, IConnector> that resolves keyed connectors by endpoint name
        services.AddSingleton<Func<string, IConnector>>(sp => name => sp.GetRequiredKeyedService<IConnector>(name));

        services.AddSingleton(sp =>
        {
            Directory.CreateDirectory(DatabaseDirectory);
            return new StateDatabase(DatabasePath, sp.GetRequiredService<ILogger<StateDatabase>>());
        });
        services.AddSingleton<EndpointCursorRepository>();
        services.AddSingleton<LinkStateRepository>();
        services.AddSingleton<LeaseRepository>();
        services.AddSingleton<OperationLogRepository>();
        services.AddSingleton<Planner>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<SyncHost>();
        services.AddSingleton<ContactsService>();
        return services;
    }

    private static IReadOnlyList<string> ScopesFor(string endpointType) => endpointType switch
    {
        EndpointOptions.GoogleContacts => ["https://www.googleapis.com/auth/contacts"],
        "google-calendar" => ["https://www.googleapis.com/auth/calendar"],
        _ => []
    };

    private static void RegisterEndpointHttpClient(IServiceCollection services, string endpointName, string endpointType)
    {
        var builder = services.AddHttpClient($"kagami-{endpointName}", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");
        });

        if (endpointType.StartsWith("Google", StringComparison.Ordinal))
        {
            builder.AddResilienceHandler("retry", b =>
            {
                b.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    UseJitter = false,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .HandleResult(r => r.StatusCode == HttpStatusCode.BadGateway)
                        .Handle<HttpRequestException>(),
                    DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                        args.AttemptNumber == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30)),
                });
            });
        }
        else if (endpointType.StartsWith("Microsoft", StringComparison.Ordinal))
        {
            builder.AddResilienceHandler("retry", b =>
            {
                b.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(4),
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .HandleResult(r => r.StatusCode == HttpStatusCode.BadGateway
                            || r.StatusCode == HttpStatusCode.ServiceUnavailable
                            || r.StatusCode == HttpStatusCode.TooManyRequests)
                        .Handle<HttpRequestException>(),
                });
            });
        }
    }
}
