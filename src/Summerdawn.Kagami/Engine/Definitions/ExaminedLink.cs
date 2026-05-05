using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents a <see cref="Link{TItem}"/> enriched with the observed activity on each side,
/// as determined by <see cref="LinkExaminer"/> independent of job policy.
/// </summary>
public sealed class ExaminedLink<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Gets the underlying link.
    /// </summary>
    public required Link<TItem> Link { get; init; }

    /// <summary>
    /// Gets the observed activity on the source side.
    /// </summary>
    public required SideActivity SourceActivity { get; init; }

    /// <summary>
    /// Gets the observed activity on the destination side.
    /// </summary>
    public required SideActivity DestinationActivity { get; init; }

    /// <summary>
    /// Returns a human-readable description of the examined link.
    /// </summary>
    public override string ToString() =>
        $"{{ Source={Link.SourceItem}, SourceActivity={SourceActivity}, " +
        $"Destination={Link.DestinationItem}, DestinationActivity={DestinationActivity} }}";
}
