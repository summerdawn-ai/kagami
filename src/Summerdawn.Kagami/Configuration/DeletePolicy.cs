namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Specifies how deletes are propagated during a sync job.
/// </summary>
public enum DeletePolicy
{
    /// <summary>
    /// Specifies that deletes are mirrored to the other side.
    /// </summary>
    Mirror,

    /// <summary>
    /// Specifies that deletes are not propagated.
    /// </summary>
    Ignore,

    /// <summary>
    /// Specifies that the other side is soft-deleted rather than hard-deleted.
    /// </summary>
    Tombstone,
}
