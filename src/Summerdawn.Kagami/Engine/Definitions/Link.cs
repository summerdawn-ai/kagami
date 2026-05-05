using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents an in-memory pairing of a source and destination item for a single sync run.
/// </summary>
/// <remarks>
/// A link may be backed by a persisted <see cref="LinkStateRow"/>, inferred by similarity
/// matching, or represent a one-sided unmatched or ambiguous item.
/// </remarks>
public sealed class Link<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Gets how this link was established.
    /// </summary>
    public required LinkKind Kind { get; init; }

    /// <summary>
    /// Gets the current source-side item, or <c>null</c> when absent from the loaded source set.
    /// </summary>
    public TItem? SourceItem { get; init; }

    /// <summary>
    /// Gets the current destination-side item, or <c>null</c> when absent from the loaded destination set.
    /// </summary>
    public TItem? DestinationItem { get; init; }

    /// <summary>
    /// Gets the persisted link state row for this pair, or <c>null</c> for inferred or unmatched links.
    /// </summary>
    public LinkStateRow? PersistedState { get; init; }

    /// <summary>
    /// Returns a human-readable description of the link.
    /// </summary>
    public override string ToString() =>
        $"{{ Kind={Kind}, Source={SourceItem}, Destination={DestinationItem} }}";
}
