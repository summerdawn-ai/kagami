
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;
/// <summary>
/// Simple duplicate matcher for canonical contacts.
/// </summary>
public sealed class ContactMatchComparer
{
    /// <summary>
    /// Returns whether the two items represent the same contact according to the current matching rules.
    /// </summary>
    public bool IsMatch(CanonicalItem leftItem, CanonicalItem rightItem)
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

        return HasOverlap(left.Emails.Select(email => NormalizeEmail(email.Address)), right.Emails.Select(email => NormalizeEmail(email.Address)))
            || HasOverlap(left.Phones.Select(phone => NormalizePhone(phone.Number)), right.Phones.Select(phone => NormalizePhone(phone.Number)));
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
