namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Host-level execution settings.
/// </summary>
public sealed class KagamiHostOptions
{
    /// <summary>
    /// Maximum number of jobs that may execute concurrently.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>
    /// How long to wait between schedule evaluation loops in run mode (seconds).
    /// </summary>
    public int SchedulerIntervalSeconds { get; set; } = 30;
}
