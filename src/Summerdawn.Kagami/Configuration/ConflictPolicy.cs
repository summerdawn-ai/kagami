namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Conflict resolution policy for a job.
/// </summary>
public enum ConflictPolicy
{
    /// <summary>
    /// The most recently modified item wins.
    /// </summary>
    LastWriteWins,

    /// <summary>
    /// The source side always wins, regardless of modification time.
    /// </summary>
    SourceWins,

    /// <summary>
    /// The destination side always wins, regardless of modification time.
    /// </summary>
    DestinationWins,

    /// <summary>
    /// Flag the conflict and skip the update without writing either side.
    /// </summary>
    Skip,
}
