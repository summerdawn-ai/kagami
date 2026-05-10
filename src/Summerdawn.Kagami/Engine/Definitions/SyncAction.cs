using Summerdawn.Kagami.Models;

using static Summerdawn.Kagami.Engine.SyncActionKind;

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
    /// Gets the origin-side item (the item whose content is being pushed to the target).
    /// </summary>
    /// <remarks>
    /// Nullability by action kind:
    /// <list type="bullet">
    ///   <item><see cref="Create"/> — always non-null; the item to be written on the target side.</item>
    ///   <item><see cref="Update"/> — always non-null; the changed item whose content drives the update.</item>
    ///   <item><see cref="Delete"/> — may be non-null but has <c>IsDeleted = true</c> (tombstone) or has moved out of filter scope; do not use it to describe or write the item. Use <see cref="GetTargetItem"/> instead for the item that will actually be deleted.</item>
    ///   <item><see cref="None"/>, <see cref="Skip"/> — may be null.</item>
    /// </list>
    /// </remarks>
    public TItem? GetOriginItem() =>
        Direction == SyncDirection.SourceToDestination
            ? Link.SourceItem
            : Link.DestinationItem;

    /// <summary>
    /// Gets the already-existing item on the target side, when it was loaded in the current run.
    /// </summary>
    /// <remarks>
    /// Nullability by action kind:
    /// <list type="bullet">
    ///   <item><see cref="Create"/> — always null; the target item does not exist yet.</item>
    ///   <item><see cref="Update"/> — non-null when the target item was directly loaded (full scan, or inferred link); null on delta runs where the target was implicitly unchanged and not returned by the connector. When null, use <see cref="Link"/>.<see cref="Link{TItem}.PersistedState"/> to obtain the provider ID and version.</item>
    ///   <item><see cref="Delete"/> — non-null on full scans and prune deletes (the live item being deleted); null on delta runs where the target was not returned by the connector. When null, use <see cref="Link"/>.<see cref="Link{TItem}.PersistedState"/> to obtain the provider ID. Prefer this over <see cref="GetOriginItem"/> for describing or identifying the deleted item, even when origin is non-null.</item>
    ///   <item><see cref="None"/>, <see cref="Skip"/> — may be null.</item>
    /// </list>
    /// </remarks>
    public TItem? GetTargetItem() =>
        Direction == SyncDirection.SourceToDestination
            ? Link.DestinationItem
            : Link.SourceItem;
}
