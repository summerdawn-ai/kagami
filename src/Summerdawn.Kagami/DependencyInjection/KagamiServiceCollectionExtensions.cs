using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.DependencyInjection;
/// <summary>
/// Extension methods for registering Kagami services.
/// </summary>
public static class KagamiServiceCollectionExtensions
{
    /// <summary>
    /// Adds Kagami core services to the DI container.
    /// </summary>
    public static IServiceCollection AddKagami(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new KagamiOptions();
        configuration.Bind(options);
        services.AddSingleton(options);

        // Build a shared HttpClient for credential/token operations (not API calls).
        HttpClient authHttpClient = new();
        authHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");
        var tokenCache = new GoogleTokenCache(options.DataDirectory);
        var credentialManager = new CredentialManager(tokenCache, authHttpClient);

        // Pre-build one credential per endpoint at startup
        foreach (var (endpointName, endpoint) in options.Endpoints)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.Credential.Type))
            {
                credentialManager.Create(endpointName, endpoint);
            }
        }
        services.AddSingleton(credentialManager);

        // Register a named HttpClient and a keyed IConnector singleton per endpoint
        services.AddHttpClient();
        services.AddTransient<MicrosoftGraphBatchThrottleHandler>();
        foreach (var (endpointName, endpoint) in options.Endpoints)
        {
            RegisterEndpointHttpClient(services, endpointName);

            string capturedEndpointName = endpointName;
            var capturedEndpoint = endpoint;
            services.AddKeyedSingleton<IConnector<CanonicalContact>>(endpointName, (sp, _) =>
            {
                var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient($"kagami-{capturedEndpointName}");
                var credential = credentialManager.Get(capturedEndpointName);
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
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a MicrosoftClientCredential.")),
                    _ => throw new InvalidOperationException(
                        $"No connector registered for endpoint '{capturedEndpointName}' of type '{capturedEndpoint.Type}'."),
                };
            });

            services.AddKeyedSingleton<IConnector<CanonicalEvent>>(endpointName, (sp, _) =>
            {
                var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient($"kagami-{capturedEndpointName}");
                var credential = credentialManager.Get(capturedEndpointName);
                return capturedEndpoint.Type switch
                {
                    EndpointOptions.GoogleEvents => new GoogleEventsConnector(
                        httpClient,
                        capturedEndpointName,
                        capturedEndpoint,
                        credential as GoogleOAuthCredential
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a GoogleOAuthCredential.")),
                    EndpointOptions.MicrosoftEvents => new MicrosoftEventsConnector(
                        httpClient,
                        capturedEndpointName,
                        capturedEndpoint,
                        credential as MicrosoftClientCredential
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a MicrosoftClientCredential.")),
                    _ => throw new InvalidOperationException(
                        $"No event connector registered for endpoint '{capturedEndpointName}' of type '{capturedEndpoint.Type}'."),
                };
            });
        }

        // Func<string, IConnector> that resolves keyed connectors by endpoint name
        services.AddSingleton<Func<string, IConnector<CanonicalContact>>>(sp => sp.GetRequiredKeyedService<IConnector<CanonicalContact>>);
        services.AddSingleton<Func<string, IConnector<CanonicalEvent>>>(sp => sp.GetRequiredKeyedService<IConnector<CanonicalEvent>>);

        services.AddSingleton(sp =>
        {
            Directory.CreateDirectory(options.DataDirectory);
            string databasePath = Path.Combine(options.DataDirectory, "sync.db");
            return new StateDatabase(databasePath, sp.GetRequiredService<ILogger<StateDatabase>>());
        });
        services.AddSingleton<EndpointCursorRepository>();
        services.AddSingleton<LinkStateRepository>();
        services.AddSingleton<LeaseRepository>();
        services.AddSingleton<OperationLogRepository>();
        services.AddSingleton<LinkCreator>();
        services.AddSingleton<SyncActionPlanner>();
        services.AddSingleton<SyncActionExecutor>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<CommandHandler<CanonicalContact>>();
        services.AddSingleton<CommandHandler<CanonicalEvent>>();
        return services;
    }

    private static void RegisterEndpointHttpClient(IServiceCollection services, string endpointName)
    {
        var builder = services.AddHttpClient($"kagami-{endpointName}", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");
        });

        builder.AddStandardResilienceHandler(options =>
        {
            // Keep total timeout above normal retry/backoff so quick transient 5xx responses surface as HTTP failures, not outer timeout exceptions.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);

            options.Retry.DelayGenerator = args =>
            {
                // Use Retry-After header if available
                var defaultDelayForTooManyRequests = TimeSpan.FromSeconds(10);

                if (args.Outcome.Result is { StatusCode: HttpStatusCode.TooManyRequests, Headers.RetryAfter: { } retryAfter })
                {
                    var delay = retryAfter?.Delta
                                ?? (retryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : defaultDelayForTooManyRequests);

                    return ValueTask.FromResult<TimeSpan?>(delay);
                }

                return ValueTask.FromResult<TimeSpan?>(defaultDelayForTooManyRequests);
            };
        });

        // ThrottleHandler runs inside (closer to the network than) the resilience handler,
        // so the synthesized 429 is visible to the resilience layer on its way back out.
        builder.AddHttpMessageHandler<MicrosoftGraphBatchThrottleHandler>();
    }
}
