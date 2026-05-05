namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Sync direction and mode policy for a job.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// Changes flow in both directions: source→destination and destination→source.
    /// </summary>
    Bidirectional,

    /// <summary>
    /// Changes flow from source to destination only.
    /// </summary>
    Forward,

    /// <summary>
    /// Changes flow from destination to source only.
    /// Kept for config-file compatibility; prefer swapping <c>--from</c>/<c>--to</c> instead.
    /// </summary>
    Reverse,
}
