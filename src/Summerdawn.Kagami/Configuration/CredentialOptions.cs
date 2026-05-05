namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Represents inline credential configuration for an endpoint.
/// </summary>
public sealed class CredentialOptions
{
    /// <summary>
    /// The credential type identifier for Google OAuth 2.0.
    /// </summary>
    public const string GoogleOAuthCredential = "GoogleOAuthCredential";

    /// <summary>
    /// The credential type identifier for Microsoft client credentials (confidential client / app-only auth).
    /// </summary>
    public const string MicrosoftClientCredential = "MicrosoftClientCredential";

    /// <summary>
    /// Gets or sets the credential type identifier.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    // ── GoogleOAuthCredential ─────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the OAuth client ID.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the OAuth client secret.
    /// </summary>
    public string? ClientSecret { get; set; }

    // ── MicrosoftClientCredential ─────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Entra tenant ID.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the path to the PFX/PKCS#12 certificate (alternative to <see cref="ClientSecret"/>).
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Gets or sets the certificate password.
    /// </summary>
    public string? CertificatePassword { get; set; }
}
