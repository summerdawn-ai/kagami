namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Represents a synchronization job definition between exactly two endpoints.
/// </summary>
public sealed class JobOptions
{
    /// <summary>
    /// Gets or sets the name of the source endpoint.
    /// </summary>
    public string SourceEndpointName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the destination endpoint.
    /// </summary>
    public string DestinationEndpointName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sync direction and mode.
    /// </summary>
    public SyncMode SyncMode { get; set; } = SyncMode.Bidirectional;

    /// <summary>
    /// Gets or sets the delete handling policy.
    /// </summary>
    public DeletePolicy DeletePolicy { get; set; } = DeletePolicy.Mirror;

    /// <summary>
    /// Gets or sets the conflict resolution policy.
    /// </summary>
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.LastWriteWins;

    /// <summary>
    /// Gets or sets a value indicating whether to ignore saved cursors and re-enumerate all items on every run.
    /// Change-detection and content-sameness checks still apply.
    /// </summary>
    public bool Full { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to ignore saved cursors and treat matched items as
    /// changed, bypassing change-detection and content-sameness checks.
    /// </summary>
    /// <remarks>
    /// Sync mode and conflict policy still select the write direction, or skip a conflict.
    /// </remarks>
    public bool Force { get; set; }

    /// <summary>
    /// Gets or sets the filter expression that scopes synchronization to a subset of items (e.g.
    /// <c>"startswith(name,'A')"</c>).  An empty or <c>null</c> value means no filter is applied.
    /// </summary>
    /// <remarks>
    /// This string also acts as the cursor scope identifier: if the filter expression changes
    /// between runs the stored cursor is discarded and the next run performs a full re-enumeration.
    /// </remarks>
    public string? Filter { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the job must not read or write any sync DB state.
    /// </summary>
    /// <remarks>
    /// When <c>true</c>, the executor skips all cursor reads and writes, all link-state reads
    /// and writes, and all operation-log entries.  Intended for import and export jobs that use
    /// a local connector and should not leave database artefacts behind.
    /// </remarks>
    public bool NoPersistence { get; set; }
}
