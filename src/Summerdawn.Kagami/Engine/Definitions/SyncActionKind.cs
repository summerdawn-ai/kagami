namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Specifies the kind of operation a sync action represents.
/// </summary>
public enum SyncActionKind
{
    /// <summary>
    /// Specifies that a new item should be created on the target side.
    /// </summary>
    Create,
    /// <summary>
    /// Specifies that an existing item on the target side should be updated.
    /// </summary>
    Update,
    /// <summary>
    /// Specifies that an item on the target side should be deleted.
    /// </summary>
    Delete,
    /// <summary>
    /// Specifies that no write should be performed, either because nothing changed or a conflict was skipped per policy.
    /// </summary>
    Skip,
}
