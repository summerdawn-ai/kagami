namespace Summerdawn.Kagami.DependencyInjection;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Persistence;

/// <summary>
/// Extension methods for registering Kagami services.
/// </summary>
public static class KagamiServiceCollectionExtensions
{
    /// <summary>Adds Kagami core services to the DI container.</summary>
    public static IServiceCollection AddKagami(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new KagamiOptions();
        configuration.Bind(options);
        services.AddSingleton(options);

        // Build a shared HttpClient for credential/token operations
        HttpClient authHttpClient = new();
        authHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("kagami/0.1");

        // Pre-build one credential per endpoint at startup
        var credentials = new Dictionary<string, IConnectorCredential>(StringComparer.OrdinalIgnoreCase);
        foreach (var (endpointName, endpoint) in options.Endpoints)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.Credential.Type))
            {
                IReadOnlyList<string> scopes = ScopesFor(endpoint.Type);
                credentials[endpointName] = CredentialFactory.Create(endpoint.Credential, authHttpClient, scopes, endpointName);
            }
        }
        services.AddSingleton(credentials);

        services.AddSingleton(sp => new StateDatabase(
            options.Persistence.DatabasePath,
            sp.GetRequiredService<ILogger<StateDatabase>>()));
        services.AddSingleton<EndpointCursorRepository>();
        services.AddSingleton<LinkStateRepository>();
        services.AddSingleton<LeaseRepository>();
        services.AddSingleton<OperationLogRepository>();
        services.AddSingleton<Planner>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<SyncHost>();
        services.AddSingleton<ContactsService>();
        services.AddSingleton<IConnectorFactory, BuiltInConnectorFactory>();
        return services;
    }

    /// <summary>Registers a custom connector factory.</summary>
    public static IServiceCollection AddConnectorFactory<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(
        this IServiceCollection services)
        where TFactory : class, IConnectorFactory
    {
        services.AddSingleton<TFactory>();
        services.AddSingleton<IConnectorFactory>(sp => sp.GetRequiredService<TFactory>());
        return services;
    }

    private static IReadOnlyList<string> ScopesFor(string endpointType) => endpointType switch
    {
        EndpointOptions.GoogleContacts => ["https://www.googleapis.com/auth/contacts"],
        "google-calendar" => ["https://www.googleapis.com/auth/calendar"],
        _ => []
    };
}
