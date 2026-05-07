using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Represents the items loaded by a connector along with the cursor to persist for a future incremental read.
/// </summary>
public sealed record ItemSet<TItem>(IReadOnlyList<TItem> Items, string? Cursor) where TItem : CanonicalItem;
