namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Credential set used by one or more endpoints.
/// </summary>
public sealed class CredentialOptions
{
    /// <summary>
    /// Credential type identifier (e.g., "google-oauth2", "graph-client-credentials").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Additional provider-specific properties.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = [];
}
