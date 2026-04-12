namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Synchronization job definition between exactly two endpoints.
/// </summary>
public sealed class JobOptions
{
    /// <summary>
    /// Whether this job is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Entity type to synchronize (e.g., "calendar-event", "contact").
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Name of endpoint A.
    /// </summary>
    public string EndpointA { get; set; } = string.Empty;

    /// <summary>
    /// Name of endpoint B.
    /// </summary>
    public string EndpointB { get; set; } = string.Empty;

    /// <summary>
    /// Sync direction/mode policy.
    /// </summary>
    public SyncMode SyncMode { get; set; } = SyncMode.Bidirectional;

    /// <summary>
    /// Delete handling policy.
    /// </summary>
    public DeletePolicy DeletePolicy { get; set; } = DeletePolicy.Mirror;

    /// <summary>
    /// Conflict resolution policy.
    /// </summary>
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.LastWriteWins;

    /// <summary>
    /// Cron or interval schedule expression (e.g., "*/5 * * * *" or "PT5M").
    /// </summary>
    public string Schedule { get; set; } = "PT15M";
}
