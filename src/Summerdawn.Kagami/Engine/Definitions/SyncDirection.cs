namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Specifies the direction of a sync action.
/// </summary>
public enum SyncDirection
{
    /// <summary>
    /// Specifies that the destination side is written from the source.
    /// </summary>
    SourceToDestination,

    /// <summary>
    /// Specifies that the source side is written from the destination.
    /// </summary>
    DestinationToSource,
}
