namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Specifies the observed activity on one side of a <see cref="Link{TItem}"/> during a sync run.
/// </summary>
public enum SideActivity
{
    /// <summary>
    /// Indicates the side was never present and has no prior persisted record.
    /// </summary>
    Absent,

    /// <summary>
    /// Indicates the item is present for the first time — no prior synced version exists for this side.
    /// </summary>
    Created,

    /// <summary>
    /// Indicates the item is present and matches the last persisted version.
    /// </summary>
    Unchanged,

    /// <summary>
    /// Indicates the item is present and differs from the last persisted version.
    /// </summary>
    Modified,

    /// <summary>
    /// Indicates the item had a persisted record but is now absent or deleted.
    /// </summary>
    Deleted,

    /// <summary>
    /// Indicates the item is visible in the current run but does not match the active filter,
    /// and it has a persisted link record — meaning it previously belonged to the synced scope
    /// but has since left it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Deleted"/> because the item still exists at the endpoint;
    /// the persisted link state must reflect the actual endpoint state and must not be written
    /// as deleted merely because the item left the filter scope.
    /// </remarks>
    MovedOutOfScope,
}
