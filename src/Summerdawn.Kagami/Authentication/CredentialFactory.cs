namespace Summerdawn.Kagami.Authentication;

using Azure.Identity;
using Summerdawn.Kagami.Configuration;
using System.Security.Cryptography.X509Certificates;

internal static class CredentialFactory
{
    public static IConnectorCredential Create(CredentialOptions options, HttpClient httpClient, IReadOnlyList<string> scopes, string endpointName)
    {
        return options.Type.ToLowerInvariant() switch
        {
            "google-oauth" => CreateGoogleOAuth(options, httpClient, scopes, endpointName),
            "graph-client-credentials" => CreateGraphClientCredentials(options),
            _ => throw new InvalidOperationException($"Unknown credential type '{options.Type}'.")
        };
    }

    private static GoogleOAuthCredential CreateGoogleOAuth(CredentialOptions options, HttpClient httpClient, IReadOnlyList<string> scopes, string endpointName)
    {
        string clientId = RequireField(options.ClientId, "ClientId", "google-oauth");
        string clientSecret = RequireField(options.ClientSecret, "ClientSecret", "google-oauth");
        return new GoogleOAuthCredential(clientId, clientSecret, endpointName, scopes, httpClient);
    }

    private static GraphClientCredential CreateGraphClientCredentials(CredentialOptions options)
    {
        string tenantId = RequireField(options.TenantId, "TenantId", "graph-client-credentials");
        string clientId = RequireField(options.ClientId, "ClientId", "graph-client-credentials");

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return new GraphClientCredential(new ClientSecretCredential(tenantId, clientId, options.ClientSecret));
        }

        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            X509Certificate2 cert = string.IsNullOrWhiteSpace(options.CertificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
            return new GraphClientCredential(new ClientCertificateCredential(tenantId, clientId, cert));
        }

        throw new InvalidOperationException("graph-client-credentials requires either 'ClientSecret' or 'CertificatePath'.");
    }

    private static string RequireField(string? value, string fieldName, string credentialType)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Credential type '{credentialType}' requires '{fieldName}'.");
        }

        return value;
    }
}
