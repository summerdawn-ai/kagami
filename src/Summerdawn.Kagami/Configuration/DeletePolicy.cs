namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Delete handling policy for a job.
/// </summary>
public enum DeletePolicy
{
    /// <summary>
    /// Mirror deletes to the other side: when an item is deleted on one side, delete it on the other.
    /// </summary>
    Mirror,

    /// <summary>
    /// Ignore deletes — do not propagate them to the other side.
    /// </summary>
    Ignore,

    /// <summary>
    /// Tombstone (soft-delete) the other side instead of hard-deleting it.
    /// </summary>
    Tombstone,
}
