
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;
/// <summary>
/// Planned sync action produced by the planner.
/// </summary>
public sealed class SyncAction
{
    /// <summary>What kind of action to perform.</summary>
    public SyncActionKind Kind { get; set; }

    /// <summary>Which side is the target of the action.</summary>
    public SyncSide TargetSide { get; set; }

    /// <summary>The canonical item to create/update (null for Delete).</summary>
    public CanonicalItem? Item { get; set; }

    /// <summary>The provider ID to delete (for Delete actions).</summary>
    public string? DeleteId { get; set; }

    /// <summary>Human-readable reason for this action.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Optional already-observed target item for update actions that are not backed by an existing link.
    /// </summary>
    public CanonicalItem? MatchedTargetItem { get; set; }
}
