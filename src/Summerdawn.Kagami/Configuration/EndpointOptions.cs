namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Named typed resource endpoint definition.
/// </summary>
public sealed class EndpointOptions
{
    /// <summary>
    /// Connector type identifier (e.g., "google-calendar", "graph-calendar", "google-contacts", "graph-contacts").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Inline credential configuration for this endpoint.
    /// </summary>
    public CredentialOptions Credential { get; set; } = new();

    /// <summary>
    /// Additional provider-specific settings (e.g., calendar ID, contacts folder).
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = [];
}
