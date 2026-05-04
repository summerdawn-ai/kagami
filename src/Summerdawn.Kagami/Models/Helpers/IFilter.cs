namespace Summerdawn.Kagami.Models;

public interface IFilter<TItem> where TItem : CanonicalItem
{
    public bool Matches(TItem item);

    /// <summary>
    /// Gets the raw filter text used to construct this filter.
    /// </summary>
    public string Scope { get; }

    public IReadOnlyList<TItem> Apply(IReadOnlyList<TItem> items);
}
