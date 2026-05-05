namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Identifies the direction of a sync action.
/// </summary>
public enum SyncDirection
{
    /// <summary>
    /// Update the destination side from the source.
    /// </summary>
    SourceToDestination,

    /// <summary>
    /// Update the source side from the destination.
    /// </summary>
    DestinationToSource,
}
