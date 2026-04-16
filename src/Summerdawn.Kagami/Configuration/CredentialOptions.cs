namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Inline credential configuration for an endpoint.
/// </summary>
public sealed class CredentialOptions
{
    /// <summary>Credential type value for Google OAuth 2.0.</summary>
    public const string GoogleOAuthCredential = "GoogleOAuthCredential";

    /// <summary>Credential type value for Microsoft client credentials (confidential client / app-only auth).</summary>
    public const string MicrosoftClientCredential = "MicrosoftClientCredential";

    /// <summary>
    /// Credential type identifier: <see cref="GoogleOAuthCredential"/> or <see cref="MicrosoftClientCredential"/>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    // ── GoogleOAuthCredential ─────────────────────────────────────────────

    /// <summary>Google OAuth client ID / Microsoft app registration client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>Google OAuth client secret / Microsoft app registration client secret.</summary>
    public string? ClientSecret { get; set; }

    // ── MicrosoftClientCredential ─────────────────────────────────────────

    /// <summary>Entra tenant ID.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional PFX/PKCS#12 certificate path (alternative to ClientSecret).</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Optional certificate password.</summary>
    public string? CertificatePassword { get; set; }
}
