namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Represents the top-level Kagami configuration, typically bound from <c>appsettings.json</c>
/// under the <c>Kagami</c> section.
/// </summary>
public sealed class KagamiOptions
{
    /// <summary>
    /// Gets the default local data directory for Kagami.
    /// </summary>
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Summerdawn.ai",
        "Kagami");

    /// <summary>
    /// Gets or sets the directory used for Kagami's local state database.
    /// </summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory;

    /// <summary>
    /// Gets or sets the named endpoint definitions, keyed by endpoint name.
    /// </summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = [];
}
