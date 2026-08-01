using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Provides matching strategies for canonical contacts and calendar events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contacts</b> use a two-step strategy: name identity followed by detail disambiguation.
/// </para>
/// <para>
/// <b>Events</b> are matched by normalized title and start value. The start is compared as a UTC
/// instant so an all-day event represented at midnight by one provider can match a timed event
/// represented at the same instant by another provider. The all-day state remains synchronized
/// content and is updated after the logical event has been matched.
/// </para>
/// <para>
/// <see cref="IsMatch"/> is a convenience wrapper that requires both steps to pass and is suitable
/// for pairwise comparisons where population context is not available.
/// </para>
/// </remarks>
public static class ItemMatcher
{
    /// <summary>
    /// Builds a candidate map from each source item's ID to its matching target items.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>
    ///     <term>Step 1 – unique name match</term>
    ///     <description>
    ///       For contacts, when exactly one source item and exactly one target item share the same primary name
    ///       (display name or organisation), they are treated as a match without requiring
    ///       overlapping identifiers. This handles contacts that only carry non-contactable data
    ///       (e.g. a LinkedIn URL) and would otherwise be incorrectly duplicated on every resync.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Step 2 – detail disambiguation</term>
    ///     <description>
    ///       For contacts, when the name group is ambiguous (multiple sources or multiple targets share the same
    ///       primary name), a second pass filters the name-group candidates by overlapping
    ///       identifiers (email, phone). Only targets that share at least one identifier with
    ///       the source item are included in the candidate set. If no identifier overlap exists
    ///       the candidate set is empty for that source item and the planner falls back to
    ///       creating a new contact.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Calendar events</term>
    ///     <description>
    ///       Events are candidates when their normalized titles and UTC start instants match. The
    ///       all-day state is intentionally excluded from identity so equivalent provider
    ///       representations can be linked and then synchronized.
    ///       Groups with multiple items on either side remain ambiguous and are not auto-linked.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    public static Dictionary<string, TItem[]> BuildDuplicateCandidateMap<TItem>(
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> targetItems) where TItem : CanonicalItem
    {
        if (typeof(TItem) == typeof(CanonicalEvent))
        {
            return BuildEventCandidateMap(sourceItems, targetItems);
        }

        // Group contact items by their normalised primary name.
        var targetsByName = targetItems
            .Where(t => t is CanonicalContact)
            .GroupBy(GetNormalizedName)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var sourceCountByName = sourceItems
            .Where(s => s is CanonicalContact)
            .GroupBy(GetNormalizedName)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var result = new Dictionary<string, TItem[]>(StringComparer.Ordinal);

        foreach (var item in sourceItems)
        {
            if (item is not CanonicalContact)
            {
                result[item.Provenance.ProviderId] = [];
                continue;
            }

            string name = GetNormalizedName(item);
            var targetsWithName = targetsByName.GetValueOrDefault(name) ?? [];
            int sourcesWithNameCount = sourceCountByName.GetValueOrDefault(name, 0);

            if (targetsWithName.Count == 0)
            {
                // No target shares this name — nothing to match.
                result[item.Provenance.ProviderId] = [];
                continue;
            }

            if (sourcesWithNameCount == 1 && targetsWithName.Count == 1)
            {
                // Step 1: unique 1:1 name match — accept without requiring overlapping identifiers.
                result[item.Provenance.ProviderId] = [targetsWithName[0]];
            }
            else
            {
                // Step 2: ambiguous name group — require at least one overlapping detail identifier.
                result[item.Provenance.ProviderId] = [.. targetsWithName.Where(t => HasDetailMatch(item, t))];
            }
        }

        return result;
    }

    /// <summary>
    /// Determines whether two items represent the same contact or calendar event.
    /// </summary>
    public static bool IsMatch(CanonicalItem leftItem, CanonicalItem rightItem)
    {
        if (leftItem is CanonicalEvent leftEvent && rightItem is CanonicalEvent rightEvent)
        {
            return GetEventIdentityKey(leftEvent).Equals(GetEventIdentityKey(rightEvent));
        }

        return HasNameMatch(leftItem, rightItem) && HasDetailMatch(leftItem, rightItem);
    }

    private static Dictionary<string, TItem[]> BuildEventCandidateMap<TItem>(
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> targetItems) where TItem : CanonicalItem
    {
        var targetsByKey = targetItems
            .Cast<CanonicalEvent>()
            .GroupBy(GetEventIdentityKey)
            .ToDictionary(group => group.Key, group => group.ToList());

        var sourceCountByKey = sourceItems
            .Cast<CanonicalEvent>()
            .GroupBy(GetEventIdentityKey)
            .ToDictionary(group => group.Key, group => group.Count());

        var result = new Dictionary<string, TItem[]>(StringComparer.Ordinal);
        foreach (var item in sourceItems.Cast<CanonicalEvent>())
        {
            var key = GetEventIdentityKey(item);
            var targetsWithKey = targetsByKey.GetValueOrDefault(key) ?? [];
            result[item.Provenance.ProviderId] = sourceCountByKey.GetValueOrDefault(key) == 1 && targetsWithKey.Count == 1
                ? [.. targetsWithKey.Cast<TItem>()]
                : [];
        }

        return result;
    }

    /// <summary>
    /// Creates the normalized identity key used to match calendar events.
    /// </summary>
    private static (string Title, long Start) GetEventIdentityKey(CanonicalEvent item) =>
        (
            NormalizeText(item.Title),
            item.From.ToUniversalTime().ToUnixTimeSeconds());

    /// <summary>
    /// Determines whether two contacts share the same primary name identity.
    /// </summary>
    public static bool HasNameMatch(CanonicalItem leftItem, CanonicalItem rightItem)
    {
        if (leftItem is not CanonicalContact left || rightItem is not CanonicalContact right)
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
    /// Determines whether two contacts share at least one overlapping identifier (email address or phone number).
    /// </summary>
    public static bool HasDetailMatch(CanonicalItem leftItem, CanonicalItem rightItem)
    {
        if (leftItem is not CanonicalContact left || rightItem is not CanonicalContact right)
        {
            return false;
        }

        return HasOverlap(left.Emails.Select(email => NormalizeEmail(email.Address)), right.Emails.Select(email => NormalizeEmail(email.Address)))
            || HasOverlap(left.Phones.Select(phone => NormalizePhone(phone.Number)), right.Phones.Select(phone => NormalizePhone(phone.Number)));
    }

    /// <summary>
    /// Returns the normalised primary name key used for name-group bucketing: display name when non-empty, otherwise organisation name.
    /// </summary>
    internal static string GetNormalizedName(CanonicalItem item)
    {
        if (item is not CanonicalContact contact)
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

    /// <summary>
    /// Determines whether an import item matches a destination item, with a fallback to display-name-only matching when neither side has contactable identifiers.
    /// </summary>
    public static bool IsImportMatch(CanonicalItem importItem, CanonicalItem destItem)
    {
        if (IsMatch(importItem, destItem))
        {
            return true;
        }

        if (importItem is CanonicalContact ic && destItem is CanonicalContact dc)
        {
            if (string.IsNullOrWhiteSpace(ic.DisplayName) || string.IsNullOrWhiteSpace(dc.DisplayName))
            {
                return false;
            }

            bool nameMatch = string.Equals(
                ic.DisplayName.Trim(),
                dc.DisplayName.Trim(),
                StringComparison.OrdinalIgnoreCase);

            bool importHasContactInfo = ic.Emails.Count > 0 || ic.Phones.Count > 0;
            bool destHasContactInfo = dc.Emails.Count > 0 || dc.Phones.Count > 0;
            return nameMatch && !importHasContactInfo && !destHasContactInfo;
        }

        return false;
    }
}
