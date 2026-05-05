using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Represents a page of incremental changes returned by a connector.
/// </summary>
public sealed class IncrementalPage<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Gets or sets the items returned in this page.
    /// </summary>
    public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the cursor to persist for the next incremental poll.
    /// </summary>
    public string? NextCursor { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether more pages are available via <see cref="NextCursor"/>.
    /// </summary>
    public bool HasMore { get; set; }
}
