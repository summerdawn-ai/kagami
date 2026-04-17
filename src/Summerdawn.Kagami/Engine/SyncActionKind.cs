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
    /// <summary>No action possible, or conflict.</summary>
    Skip,
}
