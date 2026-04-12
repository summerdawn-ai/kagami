namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Top-level configuration root for Kagami.
/// </summary>
public sealed class KagamiOptions
{
    /// <summary>
    /// Named credential sets keyed by credential name.
    /// </summary>
    public Dictionary<string, CredentialOptions> Credentials { get; set; } = [];

    /// <summary>
    /// Named endpoint definitions keyed by endpoint name.
    /// </summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = [];

    /// <summary>
    /// Named job definitions keyed by job key.
    /// </summary>
    public Dictionary<string, JobOptions> Jobs { get; set; } = [];

    /// <summary>
    /// Host-level execution settings.
    /// </summary>
    public KagamiHostOptions Host { get; set; } = new();

    /// <summary>
    /// Internal SQLite state database settings.
    /// </summary>
    public PersistenceOptions Persistence { get; set; } = new();
}
