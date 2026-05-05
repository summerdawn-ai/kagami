using System.Text.RegularExpressions;

using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Evaluates a simple OData-style filter expression against a <see cref="CanonicalContact"/>.
/// </summary>
/// <remarks>
/// Supported syntax (case-insensitive):
/// <list type="bullet">
///   <contact><c>startswith(name,'value')</c></contact>
///   <contact><c>endswith(name,'value')</c></contact>
///   <contact><c>contains(name,'value')</c></contact>
///   <contact><c>name eq 'value'</c></contact>
/// </list>
/// The identifier <c>name</c> maps to the effective contact name: <see cref="CanonicalContact.DisplayName"/>
/// when present, otherwise <see cref="CanonicalContact.Organization"/>.
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
    public static ContactFilter? Parse(string? filterExpression)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return null;
        }

        string expr = filterExpression.Trim();

        // startswith(name,'value')  — value may contain escaped quotes ('')
        var m = StartsWithPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.StartsWith(value, StringComparison.OrdinalIgnoreCase)), filterExpression);
        }

        // endswith(name,'value')
        m = EndsWithPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.EndsWith(value, StringComparison.OrdinalIgnoreCase)), filterExpression);
        }

        // contains(name,'value')
        m = ContainsPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.Contains(value, StringComparison.OrdinalIgnoreCase)), filterExpression);
        }

        // name eq 'value'
        m = EqPattern().Match(expr);
        if (m.Success)
        {
            string value = UnescapeODataString(m.Groups["val"].Value);
            return new ContactFilter(contact => MatchesName(contact, name => name.Equals(value, StringComparison.OrdinalIgnoreCase)), filterExpression);
        }

        throw new ArgumentException($"Unrecognized filter expression: '{filterExpression}'. " +
            "Supported forms: startswith(name,'x'), endswith(name,'x'), contains(name,'x'), name eq 'x'");
    }

    /// <summary>Returns true if this contact matches the filter.</summary>
    public bool Matches(CanonicalContact contact) => predicate(contact);

    /// <summary>Returns only those contacts matching the filter.</summary>
    public IReadOnlyList<CanonicalContact> Apply(IReadOnlyList<CanonicalContact> contacts) =>
        contacts.Where(predicate).ToList();

    private static bool MatchesName(CanonicalContact contact, Func<string, bool> check) =>
        check(ContactName.GetName(contact));

    private static string UnescapeODataString(string value) =>
        value.Replace("''", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"^startswith\s*\(\s*name\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex StartsWithPattern();

    [GeneratedRegex(@"^endswith\s*\(\s*name\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex EndsWithPattern();

    [GeneratedRegex(@"^contains\s*\(\s*name\s*,\s*'(?<val>(?:[^']|'')*)'\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsPattern();

    [GeneratedRegex(@"^name\s+eq\s+'(?<val>(?:[^']|'')*)'$", RegexOptions.IgnoreCase)]
    private static partial Regex EqPattern();
}
