using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents a <see cref="Link{TItem}"/> enriched with the observed activity on each side,
/// as determined by <see cref="LinkExaminer"/> independent of job policy.
/// </summary>
public sealed class ExaminedLink<TItem> : Link<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Gets the observed activity on the source side.
    /// </summary>
    public required SideActivity SourceActivity { get; init; }

    /// <summary>
    /// Gets the observed activity on the destination side.
    /// </summary>
    public required SideActivity DestinationActivity { get; init; }

    /// <summary>
    /// Gets a value indicating whether this link is relevant to the current filter scope.
    /// </summary>
    /// <remarks>
    /// When <c>false</c>, at least one side of the link is observable in this run but falls
    /// entirely outside the active filter scope and has no prior persisted record.  The planner
    /// should silently ignore this link — no action and no skip — to avoid noisy logs for items
    /// that were never part of the synced set.
    /// </remarks>
    public bool IsRelevantToCurrentScope { get; init; } = true;

    /// <summary>
    /// Returns a human-readable description of the examined link.
    /// </summary>
    public override string ToString() =>
        $"{{ Source={SourceItem}, SourceActivity={SourceActivity}, " +
        $"Destination={DestinationItem}, DestinationActivity={DestinationActivity} }}";
}
