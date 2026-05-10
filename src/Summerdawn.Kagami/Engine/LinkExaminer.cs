using Summerdawn.Kagami.Configuration;
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
    /// <param name="link">The link to examine.</param>
    /// <param name="jobOptions">
    /// Options for the current run.  When supplied, <see cref="JobOptions.Full"/> and
    /// <see cref="JobOptions.Force"/> are used to distinguish a full-scan run (absence means
    /// deleted/out-of-scope) from a delta run (absence means unchanged and still present).
    /// When <c>null</c>, full-scan semantics are assumed.
    /// </param>
    /// <param name="filter">
    /// Optional filter applied to the raw item on each side.  When an item is visible in the
    /// current scan but does not pass the filter:
    /// <list type="bullet">
    ///   <item>If a persisted link row exists → activity is <see cref="SideActivity.MovedOutOfScope"/>.</item>
    ///   <item>If no persisted link row exists → the examined link's
    ///   <see cref="ExaminedLink{TItem}.IsRelevantToCurrentScope"/> is set to <c>false</c> so the
    ///   planner silently ignores it.</item>
    /// </list>
    /// </param>
    public static ExaminedLink<TItem> Examine<TItem>(
        Link<TItem> link,
        JobOptions? jobOptions = null,
        IFilter<TItem>? filter = null) where TItem : CanonicalItem
    {
        var persistedState = link.PersistedState;
        bool isDeltaRun = jobOptions is not null && !jobOptions.Full && !jobOptions.Force;

        bool sourcePassesFilter = filter is null || (link.SourceItem is not null && filter.Matches(link.SourceItem));
        bool destPassesFilter = filter is null || (link.DestinationItem is not null && filter.Matches(link.DestinationItem));

        var sourceActivity = DetermineActivity(
            item: link.SourceItem,
            persistedVersion: persistedState?.SourceVersion,
            persistedHash: persistedState?.SourceHash,
            hadId: persistedState != null,
            isDeltaRun: isDeltaRun,
            passesFilter: sourcePassesFilter);

        var destActivity = DetermineActivity(
            item: link.DestinationItem,
            persistedVersion: persistedState?.DestinationVersion,
            persistedHash: persistedState?.DestinationHash,
            hadId: persistedState?.DestinationId != null,
            isDeltaRun: isDeltaRun,
            passesFilter: destPassesFilter);

        // When a filter is active and an item is observable but fails the filter without a
        // persisted link, the link is not part of the current sync scope at all.
        bool isRelevant = filter is null
            || persistedState != null
            || sourcePassesFilter
            || destPassesFilter;

        return new ExaminedLink<TItem>
        {
            Kind = link.Kind,
            SourceItem = link.SourceItem,
            DestinationItem = link.DestinationItem,
            PersistedState = link.PersistedState,
            SourceActivity = sourceActivity,
            DestinationActivity = destActivity,
            IsRelevantToCurrentScope = isRelevant,
        };
    }

    /// <summary>
    /// Determines the activity for one side of a link.
    /// </summary>
    /// <param name="item">The current item observed on this side, or <c>null</c> when absent.</param>
    /// <param name="persistedVersion">The version recorded in the last persisted link state, if any.</param>
    /// <param name="persistedHash">The content hash recorded in the last persisted link state, if any.</param>
    /// <param name="hadId">
    /// <c>true</c> when the persisted link state previously tracked an ID for this side.
    /// </param>
    /// <param name="isDeltaRun">
    /// <c>true</c> when the current run uses a cursor-based (delta) read.  On a delta run,
    /// absence means the item did not change since the last cursor advance and is implicitly
    /// still present; on a full-scan run, absence means the item is gone from the endpoint.
    /// </param>
    /// <param name="passesFilter">
    /// <c>true</c> when the item passes the active filter (or no filter is configured).
    /// </param>
    private static SideActivity DetermineActivity(
        CanonicalItem? item,
        string? persistedVersion,
        string? persistedHash,
        bool hadId,
        bool isDeltaRun,
        bool passesFilter)
    {
        // An explicit deletion tombstone from the provider always means Deleted, regardless
        // of whether this is a delta or a full-scan run.
        if (item is { IsDeleted: true })
        {
            return hadId ? Deleted : Absent;
        }

        bool isPresent = item is not null;

        if (!isPresent)
        {
            if (!hadId)
            {
                return Absent;
            }

            // item is null: absent from this scan.
            // On a delta run, absence means the item did not change — it is implicitly still
            // present at the endpoint.  Only a full-scan run treats absence as deletion.
            return isDeltaRun ? Unchanged : Deleted;
        }

        // Item is present. Check filter before any other activity determination.
        if (!passesFilter)
        {
            // Item is visible but outside the current filter scope.
            // MovedOutOfScope only applies when there is a prior persisted record (the item was
            // previously in scope and has now left it).  Without a prior record, the item has
            // never been part of this job's sync set, so it is treated as Absent — the caller
            // will also set IsRelevantToCurrentScope=false so the planner ignores it silently.
            return hadId ? MovedOutOfScope : Absent;
        }

        // Item is present and in scope. No prior persisted record → first time we see it.
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
