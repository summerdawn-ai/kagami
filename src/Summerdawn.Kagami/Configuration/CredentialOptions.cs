namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Inline credential configuration for an endpoint.
/// </summary>
public sealed class CredentialOptions
{
    /// <summary>
    /// Credential type identifier: "google-oauth" or "graph-client-credentials".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    // ── google-oauth ──────────────────────────────────────────────────────

    /// <summary>Google OAuth client ID / Microsoft app registration client ID.</summary>
    public string? ClientId { get; set; }

    /// <summary>Google OAuth client secret / Microsoft app registration client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Google account email used to prefill the sign-in flow and scope the local token cache.</summary>
    public string? UserLogin { get; set; }

    // ── graph-client-credentials ──────────────────────────────────────────

    /// <summary>Entra tenant ID.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional PFX/PKCS#12 certificate path (alternative to ClientSecret).</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Optional certificate password.</summary>
    public string? CertificatePassword { get; set; }
}
