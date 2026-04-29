using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
                    EndpointOptions.GoogleCalendar => new GoogleCalendarConnector(
                        httpClient,
                        capturedEndpointName,
                        capturedEndpoint,
                        credential as GoogleOAuthCredential
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a GoogleOAuthCredential."),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GoogleCalendarConnector>()),
                    EndpointOptions.MicrosoftCalendar => new MicrosoftCalendarConnector(
                        httpClient,
                        capturedEndpointName,
                        capturedEndpoint,
                        credential as MicrosoftClientCredential
                            ?? throw new InvalidOperationException($"Endpoint '{capturedEndpointName}' requires a MicrosoftClientCredential."),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MicrosoftCalendarConnector>()),
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
        services.AddSingleton<CalendarService>();
        return services;
    }

    private static IReadOnlyList<string> ScopesFor(string endpointType) => endpointType switch
    {
        EndpointOptions.GoogleContacts => ["https://www.googleapis.com/auth/contacts"],
        EndpointOptions.GoogleCalendar => ["https://www.googleapis.com/auth/calendar"],
        _ => []
    };

    private static void RegisterEndpointHttpClient(IServiceCollection services, string endpointName, string endpointType)
    {
        var builder = services.AddHttpClient($"kagami-{endpointName}", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");
        });

        builder.AddStandardResilienceHandler(options =>
        {
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
    }
}
