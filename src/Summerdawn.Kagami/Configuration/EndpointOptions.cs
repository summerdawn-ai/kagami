namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Named typed resource endpoint definition.
/// </summary>
public sealed class EndpointOptions
{
    /// <summary>
    /// Connector type value for Google Contacts (People API).
    /// </summary>
    public const string GoogleContacts = "GoogleContacts";

    /// <summary>
    /// Connector type value for Microsoft Contacts (Exchange Online / Microsoft 365).
    /// </summary>
    public const string MicrosoftContacts = "MicrosoftContacts";

    /// <summary>
    /// Connector type identifier: <see cref="GoogleContacts"/> or <see cref="MicrosoftContacts"/>.
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
