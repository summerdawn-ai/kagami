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
    /// Gets or sets the canonical item to write on the target side.
    /// </summary>
    public required TItem? Item { get; set; }

    /// <summary>
    /// Gets or sets the already-observed target item for update actions not backed by an existing link.
    /// </summary>
    public TItem? MatchedTargetItem { get; set; }

    /// <summary>
    /// Gets or sets the provider ID of the item to delete (used for Delete actions).
    /// </summary>
    public string? DeleteId { get; set; }

    /// <summary>
    /// Gets or sets a human-readable description of why this action was planned.
    /// </summary>
    public string? Reason { get; set; }
}
