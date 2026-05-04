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
    /// Name of the source endpoint.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Name of the destination endpoint.
    /// </summary>
    public string Destination { get; set; } = string.Empty;

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

    /// <summary>
    /// When <c>true</c>, ignores saved cursors and fetches all items from both sides on every run,
    /// but still applies normal change-detection and content-sameness checks.
    /// Useful when cursors are stale but contacts haven't actually changed.
    /// </summary>
    public bool Full { get; set; }

    /// <summary>
    /// When <c>true</c>, ignores cursors, bypasses the HasChanged short-circuit, and bypasses the
    /// content-sameness check — every in-scope item is written unconditionally.
    /// Use to clobber destination drift or recover from corrupted link state.
    /// </summary>
    public bool Force { get; set; }
}
