using System.Text.RegularExpressions;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Evaluates a simple OData-style filter expression against a <see cref="CanonicalContact"/>.
/// </summary>
/// <remarks>
/// Supported syntax (case-insensitive):
/// <list type="bullet">
///   <contact><c>startswith(name,'value')</c></contact>
///   <contact><c>endswith(name,'value')</c></contact>
///   <contact><c>contains(name,'value')</c></contact>
///   <contact><c>contains(categories,'value')</c></contact>
///   <contact><c>name eq 'value'</c></contact>
/// </list>
/// The identifier <c>name</c> maps to the effective contact name: <see cref="CanonicalContact.DisplayName"/>
/// when present, otherwise <see cref="CanonicalContact.Organization"/>.
/// The identifier <c>categories</c> matches exact category membership against
/// <see cref="CanonicalContact.Categories"/> using case-insensitive comparison.
/// </remarks>
public sealed partial class ContactFilter : IFilter<CanonicalContact>
{
    private readonly Func<CanonicalContact, bool> predicate;

    private ContactFilter(Func<CanonicalContact, bool> predicate, string scope)
    {
        this.predicate = predicate;
        Scope = scope;
    }

    /// <summary>
    /// Gets the raw filter text used to construct this filter.
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// Parses an OData-style filter expression and returns a <see cref="ContactFilter"/>,
    /// or <c>null</c> if the expression is null or whitespace (meaning "no filter").
    /// </summary>
    /// <param name="filterExpression">The OData-style filter expression.</param>
    /// <returns>A filter instance, or <c>null</c> if no filtering is required.</returns>
    /// <exception cref="ArgumentException">Thrown when the expression is non-empty but not recognised.</exception>
    /// <remarks>
    /// Multiple atomic expressions may be combined with a flat, case-insensitive <c>and</c>.
    /// Text inside quoted string values is not treated as an operator.
    /// </remarks>
    public static ContactFilter? Parse(string? filterExpression)
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
            return new ContactFilter(contact => filters.All(filter => filter.Matches(contact)), filterExpression);
        }

        return ParseClause(expr, filterExpression);
    }

    /// <summary>
    /// Parses one atomic contact filter clause.
    /// </summary>
    /// <param name="expression">The atomic clause to parse.</param>
    /// <param name="scope">The complete filter expression to retain as the filter scope.</param>
    /// <returns>A filter for the atomic clause.</returns>
    private static ContactFilter ParseClause(string expression, string? scope = null)
    {
        string filterScope = scope ?? expression;

        // startswith(name,'value')  — value may contain escaped quotes ('')
        var m = StartsWithPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.StartsWith(value, StringComparison.OrdinalIgnoreCase)), filterScope);
        }

        // endswith(name,'value')
        m = EndsWithPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.EndsWith(value, StringComparison.OrdinalIgnoreCase)), filterScope);
        }

        // contains(name,'value')
        m = ContainsPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.Contains(value, StringComparison.OrdinalIgnoreCase)), filterScope);
        }

        // contains(categories,'value')
        m = ContainsCategoriesPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => contact.Categories.Any(category => string.Equals(category, value, StringComparison.OrdinalIgnoreCase)), filterScope);
        }

        // name eq 'value'
        m = EqPattern().Match(expression);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.Equals(value, StringComparison.OrdinalIgnoreCase)), filterScope);
        }

        throw new ArgumentException($"Unrecognized filter expression: '{expression}'. " +
            "Supported forms: startswith(name,'x'), endswith(name,'x'), contains(name,'x'), contains(categories,'x'), name eq 'x'");
    }

    /// <summary>Returns true if this contact matches the filter.</summary>
    public bool Matches(CanonicalContact contact) => predicate(contact);

    /// <summary>Returns only those contacts matching the filter.</summary>
    public IReadOnlyList<CanonicalContact> Apply(IReadOnlyList<CanonicalContact> contacts) =>
        contacts.Where(predicate).ToList();

    private static bool MatchesName(CanonicalContact contact, Func<string, bool> check) =>
        check(ContactNameHelper.GetName(contact));

    private static string UnescapeODataString(string value) =>
        value.Replace("''", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"^startswith\s*\(\s*name\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithPattern();

    [GeneratedRegex(@"^endswith\s*\(\s*name\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex EndsWithPattern();

    [GeneratedRegex(@"^contains\s*\(\s*name\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsPattern();

    [GeneratedRegex(@"^contains\s*\(\s*categories\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsCategoriesPattern();

    [GeneratedRegex(@"^name\s+eq\s+'(?<val>(?:[^']|'')*)'$", RegexOptions.IgnoreCase)]
    private static partial Regex EqPattern();
}
