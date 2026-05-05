using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// A page of incremental changes returned by a connector.
/// </summary>
public sealed class IncrementalPage<TItem> where TItem : CanonicalItem
{
    /// <summary>Items returned in this page.</summary>
    public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>New cursor/sync token to persist for the next incremental poll.</summary>
    public string? NextCursor { get; set; }

    /// <summary>Whether there are more pages to fetch.</summary>
    public bool HasMore { get; set; }
}
