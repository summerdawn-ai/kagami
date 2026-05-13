namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Specifies the direction and mode in which items are synchronized.
/// </summary>
public enum SyncMode
{
    /// <summary>
    /// Specifies that changes flow in both directions.
    /// </summary>
    Bidirectional,

    /// <summary>
    /// Specifies that changes flow from source to destination only.
    /// </summary>
    Forward,

    /// <summary>
    /// Specifies that changes flow from destination to source only.
    /// Kept for config-file compatibility; prefer swapping <c>--from</c>/<c>--to</c> instead.
    /// </summary>
    Reverse,
}
