namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Represents the top-level Kagami configuration, typically bound from <c>appsettings.json</c>
/// under the <c>Kagami</c> section.
/// </summary>
public sealed class KagamiOptions
{
    /// <summary>
    /// Gets or sets the named endpoint definitions, keyed by endpoint name.
    /// </summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = [];

    /// <summary>
    /// Gets or sets the named job definitions, keyed by job key.
    /// </summary>
    public Dictionary<string, JobOptions> Jobs { get; set; } = [];

    /// <summary>
    /// Gets or sets the host-level execution settings.
    /// </summary>
    public KagamiHostOptions Host { get; set; } = new();
}
