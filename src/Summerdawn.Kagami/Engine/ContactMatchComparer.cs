using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Two-step matcher for canonical contacts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Step 1 – name identity</b> (<see cref="HasNameMatch"/>): contacts are compared by display
/// name, falling back to organisation name when display name is absent on both sides.
/// </para>
/// <para>
/// <b>Step 2 – detail disambiguation</b> (<see cref="HasDetailMatch"/>): contacts are compared by
/// overlapping identifiers (email addresses, phone numbers).
/// </para>
/// <para>
/// <see cref="IsMatch"/> is a convenience wrapper that requires both steps to pass and is suitable
/// for pairwise comparisons where population context is not available.
/// </para>
/// </remarks>
public sealed class ContactMatchComparer
{
    /// <summary>
    /// Returns whether the two items represent the same contact: name identity plus at least one
    /// overlapping identifier (email or phone).
    /// Equivalent to <c>HasNameMatch(left, right) &amp;&amp; HasDetailMatch(left, right)</c>.
    /// </summary>
    public bool IsMatch(CanonicalItem leftItem, CanonicalItem rightItem) =>
        HasNameMatch(leftItem, rightItem) && HasDetailMatch(leftItem, rightItem);

    /// <summary>
    /// Returns whether the two contacts share the same primary name identity (display name,
    /// or organisation when display name is absent on both sides).
    /// </summary>
    public bool HasNameMatch(CanonicalItem leftItem, CanonicalItem rightItem)
    {
        if (leftItem.Payload is not CanonicalContact left || rightItem.Payload is not CanonicalContact right)
        {
            return false;
        }

        string leftDisplayName = NormalizeText(left.DisplayName);
        string rightDisplayName = NormalizeText(right.DisplayName);
        if (!string.Equals(leftDisplayName, rightDisplayName, StringComparison.Ordinal))
        {
            return false;
        }

        if (leftDisplayName.Length == 0)
        {
            string leftOrganization = NormalizeText(left.Organization);
            string rightOrganization = NormalizeText(right.Organization);
            if (!string.Equals(leftOrganization, rightOrganization, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether the two contacts share at least one overlapping identifier
    /// (email address or phone number).
    /// </summary>
    public bool HasDetailMatch(CanonicalItem leftItem, CanonicalItem rightItem)
    {
        if (leftItem.Payload is not CanonicalContact left || rightItem.Payload is not CanonicalContact right)
        {
            return false;
        }

        return HasOverlap(left.Emails.Select(email => NormalizeEmail(email.Address)), right.Emails.Select(email => NormalizeEmail(email.Address)))
            || HasOverlap(left.Phones.Select(phone => NormalizePhone(phone.Number)), right.Phones.Select(phone => NormalizePhone(phone.Number)));
    }

    /// <summary>
    /// Returns the normalised primary name key used for name-group bucketing during duplicate
    /// detection. If the display name is non-empty it is returned; otherwise the organisation
    /// name is used.
    /// </summary>
    internal static string GetNormalizedName(CanonicalItem item)
    {
        if (item.Payload is not CanonicalContact contact)
        {
            return string.Empty;
        }

        string displayName = NormalizeText(contact.DisplayName);
        return displayName.Length > 0 ? displayName : NormalizeText(contact.Organization);
    }

    private static bool HasOverlap(IEnumerable<string> leftValues, IEnumerable<string> rightValues)
    {
        HashSet<string> right = [.. rightValues.Where(value => value.Length > 0)];
        return leftValues.Where(value => value.Length > 0).Any(right.Contains);
    }

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeEmail(string? value) => NormalizeText(value);

    private static string NormalizePhone(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());
}
