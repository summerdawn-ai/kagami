namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Kind of sync action.
/// </summary>
public enum SyncActionKind
{
    /// <summary>Create a new item on the target side.</summary>
    Create,
    /// <summary>Update an existing item on the target side.</summary>
    Update,
    /// <summary>Delete an item on the target side.</summary>
    Delete,
    /// <summary>No action needed (already in sync).</summary>
    NoOp,
    /// <summary>Conflict detected — requires manual resolution or policy.</summary>
    Conflict,
}
