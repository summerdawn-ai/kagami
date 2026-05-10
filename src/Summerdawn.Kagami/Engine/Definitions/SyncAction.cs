using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents a planned sync action produced by the planner.
/// </summary>
public sealed class SyncAction<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Gets or sets the kind of operation to perform.
    /// </summary>
    public required SyncActionKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the direction of the action.
    /// </summary>
    public required SyncDirection Direction { get; set; }

    /// <summary>
    /// Gets or sets the examined link context that led to this action.
    /// </summary>
    public required ExaminedLink<TItem> Link { get; set; }

    /// <summary>
    /// Gets or sets a human-readable description of why this action was planned.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets the origin-side item, when available.
    /// </summary>
    public TItem? GetOriginItem() =>
        Direction == SyncDirection.SourceToDestination
            ? Link.SourceItem
            : Link.DestinationItem;

    /// <summary>
    /// Gets the target-side item, when available.
    /// </summary>
    public TItem? GetTargetItem() =>
        Direction == SyncDirection.SourceToDestination
            ? Link.DestinationItem
            : Link.SourceItem;
}
