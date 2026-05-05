namespace Summerdawn.Kagami.Models;

/// <summary>
/// Defines a predicate filter over a sequence of <typeparamref name="TItem"/> instances.
/// </summary>
/// <remarks>
/// Implementors should also expose a <see cref="Scope"/> string that identifies the filter
/// expression used, so that cursors tied to a different scope can be invalidated.
/// </remarks>
public interface IFilter<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Determines whether <paramref name="item"/> matches the filter.
    /// </summary>
    public bool Matches(TItem item);

    /// <summary>
    /// Gets the raw filter expression that uniquely identifies this filter's scope.
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// Returns a new list containing only the items that match the filter.
    /// </summary>
    public IReadOnlyList<TItem> Apply(IReadOnlyList<TItem> items);
}
