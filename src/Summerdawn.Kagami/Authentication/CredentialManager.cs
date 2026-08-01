using System.Security.Cryptography.X509Certificates;

using Azure.Identity;

using Summerdawn.Kagami.Configuration;

namespace Summerdawn.Kagami.Authentication;

/// <summary>
/// Creates and manages the credentials configured for Kagami endpoints.
/// </summary>
internal sealed class CredentialManager(GoogleTokenCache tokenCache, HttpClient httpClient)
{
    private readonly Dictionary<string, IConnectorCredential> credentials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates and retains the credential for an endpoint.
    /// </summary>
    public IConnectorCredential Create(string endpointName, EndpointOptions endpoint)
    {
        IConnectorCredential credential = endpoint.Credential.Type switch
        {
            CredentialOptions.GoogleOAuthCredential => CreateGoogleOAuth(endpointName, endpoint),
            CredentialOptions.MicrosoftClientCredential => CreateMicrosoftClientCredential(endpoint.Credential),
            _ => throw new InvalidOperationException($"Unknown credential type '{endpoint.Credential.Type}'.")
        };

        credentials[endpointName] = credential;
        return credential;
    }

    /// <summary>
    /// Gets the retained credential for an endpoint.
    /// </summary>
    public IConnectorCredential Get(string endpointName) => credentials.TryGetValue(endpointName, out var credential)
        ? credential
        : throw new InvalidOperationException($"Endpoint '{endpointName}' has no configured credential.");

    /// <summary>
    /// Reports the configured credential and cached-token status for each endpoint.
    /// </summary>
    public IReadOnlyList<EndpointCredentialStatus> List(IReadOnlyDictionary<string, EndpointOptions> endpoints) =>
        endpoints
            .Select(pair =>
            {
                string? status = pair.Value.Credential.Type switch
                {
                    CredentialOptions.GoogleOAuthCredential => tokenCache.Load(pair.Key) is { AccessToken: not null } ? "Token cached" : "No token cached",
                    CredentialOptions.MicrosoftClientCredential => "Not applicable",
                    _ => "Unknown",
                };

                return new EndpointCredentialStatus(pair.Key, pair.Value.Type, pair.Value.Credential.Type, status);
            })
            .OrderBy(status => status.EndpointName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Gets a Google access token, using the cache or interactive authorization as required.
    /// </summary>
    public async Task<CredentialOperationResult> LoginAsync(string endpointName, EndpointOptions endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint.Credential.Type == CredentialOptions.MicrosoftClientCredential)
        {
            return new CredentialOperationResult("This credential does not require login.");
        }

        if (Get(endpointName) is not GoogleOAuthCredential credential)
        {
            throw new InvalidOperationException($"Endpoint '{endpointName}' does not support login.");
        }

        _ = await credential.GetAccessTokenAsync(cancellationToken);
        return new CredentialOperationResult("Google login successful.");
    }

    /// <summary>
    /// Removes the cached Google token for an endpoint.
    /// </summary>
    public CredentialOperationResult Logout(string endpointName, EndpointOptions endpoint)
    {
        if (endpoint.Credential.Type == CredentialOptions.MicrosoftClientCredential)
        {
            return new CredentialOperationResult("The credential type for this endpoint does not support logout.");
        }

        if (Get(endpointName) is GoogleOAuthCredential credential)
        {
            return new CredentialOperationResult(credential.Logout()
                ? "Google credential removed from cache."
                : "No cached Google credential found.");
        }

        throw new InvalidOperationException($"Endpoint '{endpointName}' does not support logout.");
    }

    private GoogleOAuthCredential CreateGoogleOAuth(string endpointName, EndpointOptions endpoint)
    {
        string clientId = RequireField(endpoint.Credential.ClientId, "ClientId", CredentialOptions.GoogleOAuthCredential);
        string clientSecret = RequireField(endpoint.Credential.ClientSecret, "ClientSecret", CredentialOptions.GoogleOAuthCredential);
        string expectedUserId = RequireField(endpoint.Properties.GetOptionalValue("userId"), "userId", CredentialOptions.GoogleOAuthCredential);
        return new GoogleOAuthCredential(clientId, clientSecret, endpointName, expectedUserId, ScopesFor(endpoint.Type), httpClient, tokenCache);
    }

    private static MicrosoftClientCredential CreateMicrosoftClientCredential(CredentialOptions options)
    {
        string tenantId = RequireField(options.TenantId, "TenantId", CredentialOptions.MicrosoftClientCredential);
        string clientId = RequireField(options.ClientId, "ClientId", CredentialOptions.MicrosoftClientCredential);

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return new MicrosoftClientCredential(new ClientSecretCredential(tenantId, clientId, options.ClientSecret));
        }

        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            var cert = string.IsNullOrWhiteSpace(options.CertificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
            return new MicrosoftClientCredential(new ClientCertificateCredential(tenantId, clientId, cert));
        }

        throw new InvalidOperationException($"{CredentialOptions.MicrosoftClientCredential} requires either 'ClientSecret' or 'CertificatePath'.");
    }

    private static IReadOnlyList<string> ScopesFor(string endpointType) => endpointType switch
    {
        EndpointOptions.Google => ["https://www.googleapis.com/auth/contacts", "https://www.googleapis.com/auth/calendar", "openid", "email"],
        EndpointOptions.GoogleContacts => ["https://www.googleapis.com/auth/contacts", "openid", "email"],
        EndpointOptions.GoogleEvents => ["https://www.googleapis.com/auth/calendar", "openid", "email"],
        _ => [],
    };

    private static string RequireField(string? value, string fieldName, string credentialType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Credential type '{credentialType}' requires '{fieldName}'.");
        }

        return value;
    }
}

/// <summary>
/// Represents the credential and cached-token status of a configured endpoint.
/// </summary>
internal sealed record EndpointCredentialStatus(
    string EndpointName,
    string EndpointType,
    string CredentialType,
    string Status);

/// <summary>
/// Represents the result of an endpoint credential operation.
/// </summary>
internal sealed record CredentialOperationResult(string Message);
