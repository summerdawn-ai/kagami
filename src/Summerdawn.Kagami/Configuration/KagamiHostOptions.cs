namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Represents host-level execution settings.
/// </summary>
public sealed class KagamiHostOptions
{
    /// <summary>
    /// Gets or sets the maximum number of jobs that may execute concurrently.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;


}
