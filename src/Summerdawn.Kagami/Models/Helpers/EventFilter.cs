using System.Text.RegularExpressions;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Evaluates a simple OData-style filter expression against a <see cref="CanonicalEvent"/>.
/// </summary>
/// <remarks>
/// Supported syntax (case-insensitive):
/// <list type="bullet">
///   <item><c>startswith(title,'value')</c></item>
///   <item><c>endswith(title,'value')</c></item>
///   <item><c>contains(title,'value')</c></item>
///   <item><c>title eq 'value'</c></item>
/// </list>
/// </remarks>
public sealed partial class EventFilter : IFilter<CanonicalEvent>
{
    private readonly Func<CanonicalEvent, bool> predicate;

    private EventFilter(Func<CanonicalEvent, bool> predicate, string scope)
    {
        this.predicate = predicate;
        Scope = scope;
    }

    /// <summary>
    /// Gets the raw filter text used to construct this filter.
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// Parses an OData-style filter expression and returns a <see cref="EventFilter"/>,
    /// or <c>null</c> if the expression is null or whitespace.
    /// </summary>
    public static EventFilter? Parse(string? filterExpression)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return null;
        }

        string expr = filterExpression.Trim();

        var m = StartsWithPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.StartsWith(value, StringComparison.OrdinalIgnoreCase), filterExpression);
        }

        m = EndsWithPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.EndsWith(value, StringComparison.OrdinalIgnoreCase), filterExpression);
        }

        m = ContainsPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.Contains(value, StringComparison.OrdinalIgnoreCase), filterExpression);
        }

        m = EqPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.Equals(value, StringComparison.OrdinalIgnoreCase), filterExpression);
        }

        throw new ArgumentException($"Unrecognized filter expression: '{filterExpression}'. " +
            "Supported forms: startswith(title,'x'), endswith(title,'x'), contains(title,'x'), title eq 'x'");
    }

    /// <summary>
    /// Determines whether an event matches this filter.
    /// </summary>
    public bool Matches(CanonicalEvent item) => predicate(item);

    private static string UnescapeODataString(string value) =>
        value.Replace("''", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"^startswith\s*\(\s*title\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithPattern();

    [GeneratedRegex(@"^endswith\s*\(\s*title\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex EndsWithPattern();

    [GeneratedRegex(@"^contains\s*\(\s*title\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsPattern();

    [GeneratedRegex(@"^title\s+eq\s+'(?<val>(?:[^']|'')*)'$", RegexOptions.IgnoreCase)]
    private static partial Regex EqPattern();

    public IReadOnlyList<CanonicalEvent> Apply(IReadOnlyList<CanonicalEvent> items)
    {
        return items.Where(predicate).ToList();
    }
}
