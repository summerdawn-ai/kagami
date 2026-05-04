namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Identifies the direction of a sync action.
/// </summary>
public enum SyncDirection
{
    /// <summary>Update Destination side from Source.</summary>
    SourceToDestination,

    /// <summary>Update Source side from Destination.</summary>
    DestinationToSource,
}
