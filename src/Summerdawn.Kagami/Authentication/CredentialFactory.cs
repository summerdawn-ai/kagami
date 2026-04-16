namespace Summerdawn.Kagami.Authentication;

using System.Security.Cryptography.X509Certificates;

using Azure.Identity;

using Summerdawn.Kagami.Configuration;

internal static class CredentialFactory
{
    public static IConnectorCredential Create(CredentialOptions options, HttpClient httpClient, IReadOnlyList<string> scopes, string endpointName)
    {
        return options.Type switch
        {
            CredentialOptions.GoogleOAuthCredential => CreateGoogleOAuth(options, httpClient, scopes, endpointName),
            CredentialOptions.MicrosoftClientCredential => CreateMicrosoftClientCredential(options),
            _ => throw new InvalidOperationException($"Unknown credential type '{options.Type}'.")
        };
    }

    private static GoogleOAuthCredential CreateGoogleOAuth(CredentialOptions options, HttpClient httpClient, IReadOnlyList<string> scopes, string endpointName)
    {
        string clientId = RequireField(options.ClientId, "ClientId", CredentialOptions.GoogleOAuthCredential);
        string clientSecret = RequireField(options.ClientSecret, "ClientSecret", CredentialOptions.GoogleOAuthCredential);
        return new GoogleOAuthCredential(clientId, clientSecret, endpointName, scopes, httpClient);
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
            X509Certificate2 cert = string.IsNullOrWhiteSpace(options.CertificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
            return new MicrosoftClientCredential(new ClientCertificateCredential(tenantId, clientId, cert));
        }

        throw new InvalidOperationException($"{CredentialOptions.MicrosoftClientCredential} requires either 'ClientSecret' or 'CertificatePath'.");
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
