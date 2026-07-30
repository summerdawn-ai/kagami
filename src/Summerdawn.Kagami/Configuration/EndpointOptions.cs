namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Represents a named, typed endpoint configuration.
/// </summary>
public sealed class EndpointOptions
{
    /// <summary>
    /// The connector type identifier for Google contacts (People API).
    /// </summary>
    public const string GoogleContacts = "GoogleContacts";

    /// <summary>
    /// The connector type identifier for Microsoft contacts (Exchange Online / Microsoft 365).
    /// </summary>
    public const string MicrosoftContacts = "MicrosoftContacts";

    /// <summary>
    /// The connector type identifier for Google events.
    /// </summary>
    public const string GoogleEvents = "GoogleEvents";

    /// <summary>
    /// The connector type identifier for Microsoft events (Exchange Online / Microsoft 365).
    /// </summary>
    public const string MicrosoftEvents = "MicrosoftEvents";

    /// <summary>
    /// Gets or sets the connector type identifier.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the inline credential configuration.
    /// </summary>
    public CredentialOptions Credential { get; set; } = new();

    /// <summary>
    /// Gets or sets additional provider-specific settings (e.g., calendar ID, contacts folder).
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = [];
}
