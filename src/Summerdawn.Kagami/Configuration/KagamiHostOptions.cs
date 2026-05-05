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

    /// <summary>
    /// Gets or sets the interval between schedule evaluation loops in run mode, in seconds.
    /// </summary>
    public int SchedulerIntervalSeconds { get; set; } = 30;
}
