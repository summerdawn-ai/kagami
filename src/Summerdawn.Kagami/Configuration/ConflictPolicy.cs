namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Specifies how conflicts between source and destination are resolved.
/// </summary>
public enum ConflictPolicy
{
    /// <summary>
    /// Specifies that the item with the most recent modification timestamp wins.
    /// </summary>
    LastWriteWins,

    /// <summary>
    /// Specifies that the source side always wins, regardless of modification time.
    /// </summary>
    SourceWins,

    /// <summary>
    /// Specifies that the destination side always wins, regardless of modification time.
    /// </summary>
    DestinationWins,

    /// <summary>
    /// Specifies that conflicting items are skipped without writing either side.
    /// </summary>
    Skip,
}
