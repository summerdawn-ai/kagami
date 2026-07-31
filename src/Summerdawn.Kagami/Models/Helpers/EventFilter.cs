using System.Globalization;
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
///   <item><c>start gt '2026-01-01T00:00:00Z'</c></item>
///   <item><c>end lt '2026-02-01T00:00:00Z'</c></item>
///   <item><c>filter1 and filter2</c></item>
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
    /// <remarks>
    /// Multiple atomic expressions may be combined with a flat, case-insensitive <c>and</c>.
    /// Text inside quoted string values is not treated as an operator.
    /// </remarks>
    public static EventFilter? Parse(string? filterExpression)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return null;
        }

        string expr = filterExpression.Trim();
        var clauses = FilterExpressionParser.SplitAnd(expr);
        if (clauses.Count > 1)
        {
            var filters = clauses.Select(clause => ParseClause(clause)).ToArray();
            return new EventFilter(item => filters.All(filter => filter.Matches(item)), filterExpression);
        }

        return ParseClause(expr, filterExpression);
    }

    /// <summary>
    /// Parses one atomic event filter clause.
    /// </summary>
    /// <param name="expression">The atomic clause to parse.</param>
    /// <param name="scope">The complete filter expression to retain as the filter scope.</param>
    /// <returns>A filter for the atomic clause.</returns>
    private static EventFilter ParseClause(string expression, string? scope = null)
    {
        string filterScope = scope ?? expression;

        var m = StartsWithPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.StartsWith(value, StringComparison.OrdinalIgnoreCase), filterScope);
        }

        m = EndsWithPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.EndsWith(value, StringComparison.OrdinalIgnoreCase), filterScope);
        }

        m = ContainsPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.Contains(value, StringComparison.OrdinalIgnoreCase), filterScope);
        }

        m = EqPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new EventFilter(e => e.Title.Equals(value, StringComparison.OrdinalIgnoreCase), filterScope);
        }

        m = DatePattern().Match(expression);
        if (m.Success && DateTimeOffset.TryParse(m.Groups["value"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
        {
            bool isStart = m.Groups["field"].Value.Equals("start", StringComparison.OrdinalIgnoreCase);
            bool isGreaterThan = m.Groups["operator"].Value.Equals("gt", StringComparison.OrdinalIgnoreCase);
            return new EventFilter(item => isStart
                ? isGreaterThan ? item.From > date : item.From < date
                : isGreaterThan ? item.To > date : item.To < date, filterScope);
        }

        throw new ArgumentException($"Unrecognized filter expression: '{expression}'. " +
            "Supported forms: startswith(title,'x'), endswith(title,'x'), contains(title,'x'), title eq 'x', start gt 'date', end lt 'date'");
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

    [GeneratedRegex(@"^(?<field>start|end)\s+(?<operator>lt|gt)\s+'(?<value>[^']+)'$", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    public IReadOnlyList<CanonicalEvent> Apply(IReadOnlyList<CanonicalEvent> items)
    {
        return items.Where(predicate).ToList();
    }
}
