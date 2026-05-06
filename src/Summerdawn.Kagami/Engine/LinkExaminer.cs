using Summerdawn.Kagami.Models;

using static Summerdawn.Kagami.Engine.SideActivity;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Examines a single <see cref="Link{TItem}"/> and determines what has happened on each side,
/// independent of job policy.
/// </summary>
public static class LinkExaminer
{
    /// <summary>
    /// Examines <paramref name="link"/> and returns an <see cref="ExaminedLink{TItem}"/> that
    /// describes the observed activity on each side.
    /// </summary>
    public static ExaminedLink<TItem> Examine<TItem>(Link<TItem> link) where TItem : CanonicalItem
    {
        var persistedState = link.PersistedState;

        return new ExaminedLink<TItem>
        {
            Link = link,
            SourceActivity = DetermineActivity(
                item: link.SourceItem,
                persistedVersion: persistedState?.SourceVersion,
                persistedHash: persistedState?.SourceHash,
                hadId: persistedState != null),
            DestinationActivity = DetermineActivity(
                item: link.DestinationItem,
                persistedVersion: persistedState?.DestinationVersion,
                persistedHash: persistedState?.DestinationHash,
                hadId: persistedState?.DestinationId != null),
        };
    }

    /// <summary>
    /// Determines the activity for one side of a link.
    /// </summary>
    /// <param name="item">The current item observed on this side, or <c>null</c> when absent.</param>
    /// <param name="persistedVersion">The version recorded in the last persisted link state, if any.</param>
    /// <param name="persistedHash">The content hash recorded in the last persisted link state, if any.</param>
    /// <param name="hadId">
    /// <c>true</c> when the persisted link state previously tracked an ID for this side,
    /// meaning absence should be reported as <see cref="SideActivity.Deleted"/> rather than
    /// <see cref="SideActivity.Absent"/>.
    /// </param>
    private static SideActivity DetermineActivity(
        CanonicalItem? item,
        string? persistedVersion,
        string? persistedHash,
        bool hadId)
    {
        bool isPresent = item is { IsDeleted: false };

        if (!isPresent)
        {
            return hadId ? Deleted : Absent;
        }

        // Item is present. No prior persisted record → first time we see it.
        if (!hadId)
        {
            return Created;
        }

        // Compare against persisted version/hash.
        return HasChanged(item!, persistedVersion, persistedHash) ? Modified : Unchanged;
    }

    /// <summary>
    /// Determines whether <paramref name="item"/> has changed relative to the given persisted version or hash.
    /// </summary>
    /// <remarks>
    /// Version is checked first; content hash is used as a fallback when version is unavailable.
    /// When neither is present on either side, the method conservatively returns <c>true</c>
    /// (assume changed) to avoid silently dropping updates.
    /// </remarks>
    private static bool HasChanged(CanonicalItem item, string? persistedVersion, string? persistedHash)
    {
        if (item.Provenance.Version != null && persistedVersion != null)
        {
            return item.Provenance.Version != persistedVersion;
        }

        if (item.Provenance.ContentHash != null && persistedHash != null)
        {
            return item.Provenance.ContentHash != persistedHash;
        }

        // No version or hash available — conservatively assume changed.
        return true;
    }
}
