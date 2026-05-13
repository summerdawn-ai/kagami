using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Builds a set of in-memory <see cref="Link{TItem}"/> instances from the loaded source and
/// destination item sets and the relevant persisted link state rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relevant link rows</b> are those whose <c>SourceId</c> appears in the loaded source items
/// or whose <c>DestinationId</c> appears in the loaded destination items. Rows where neither ID
/// is present in the current scan are untouched by this run and are ignored entirely.
/// </para>
/// <para>
/// Link assembly is performed in three passes:
/// </para>
/// <list type="number">
///   <item>
///     <term>Pass 1 – Persisted links</term>
///     <description>One <see cref="Link{TItem}"/> with <see cref="LinkKind.Persisted"/> is created
///     per relevant link row.  Both endpoints are reserved so they never appear in subsequent
///     matching passes.</description>
///   </item>
///   <item>
///     <term>Pass 2 – Unlinked source items</term>
///     <description>Each non-deleted source item that has not been reserved is matched against
///     eligible destination items using <see cref="ItemMatcher"/>.  The result is an
///     <see cref="LinkKind.Inferred"/> link (clean 1:1 match), an
///     <see cref="LinkKind.Ambiguous"/> link (multiple candidates or competition), or an
///     <see cref="LinkKind.Unmatched"/> link (no candidates).</description>
///   </item>
///   <item>
///     <term>Pass 3 – Remaining unlinked destination items</term>
///     <description>Any non-deleted destination item that was not reserved or matched in Pass 2
///     becomes an <see cref="LinkKind.Unmatched"/> link with only a destination side.</description>
///   </item>
/// </list>
/// </remarks>
public sealed class LinkCreator(ILogger<LinkCreator> logger)
{
    /// <summary>
    /// Builds a list of <see cref="Link{TItem}"/> instances from the given item sets and
    /// relevant persisted link state rows.
    /// </summary>
    public IReadOnlyList<Link<TItem>> BuildLinks<TItem>(
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> destinationItems,
        IReadOnlyList<LinkStateRow> allPersistedRows) where TItem : CanonicalItem
    {
        var links = new List<Link<TItem>>();

        var sourceItemsById = sourceItems.ToDictionary(i => i.Provenance.ProviderId, StringComparer.Ordinal);
        var destinationItemsById = destinationItems.ToDictionary(i => i.Provenance.ProviderId, StringComparer.Ordinal);

        var reservedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var reservedDestinationIds = new HashSet<string>(StringComparer.Ordinal);

        // -----------------------------------------------------------------------
        // Pass 1: Persisted links
        // A link row is "relevant" when its source or destination ID appears in the
        // loaded item sets. Rows for items outside the current scan are untouched.
        // -----------------------------------------------------------------------
        foreach (var row in allPersistedRows)
        {
            bool sourceLoaded = sourceItemsById.ContainsKey(row.SourceId);
            bool destLoaded = row.DestinationId != null && destinationItemsById.ContainsKey(row.DestinationId);

            if (!sourceLoaded && !destLoaded)
            {
                // Neither side visible in this scan — skip entirely.
                continue;
            }

            sourceItemsById.TryGetValue(row.SourceId, out var sourceItem);
            var destinationItem = row.DestinationId != null ? destinationItemsById.GetValueOrDefault(row.DestinationId) : null;

            reservedSourceIds.Add(row.SourceId);
            if (row.DestinationId != null)
            {
                reservedDestinationIds.Add(row.DestinationId);
            }

            links.Add(new Link<TItem>
            {
                Kind = LinkKind.Persisted,
                SourceItem = sourceItem,
                DestinationItem = destinationItem,
                PersistedState = row,
            });
            logger.LogDebug(
                "Persisted link: sourceId={SourceId}, destinationId={DestinationId}, sourceLoaded={SourceLoaded}, destinationLoaded={DestinationLoaded}",
                row.SourceId,
                row.DestinationId,
                sourceLoaded,
                destLoaded);
        }

        // -----------------------------------------------------------------------
        // Pass 2: Unlinked source items → match against eligible destination items
        // -----------------------------------------------------------------------
        var unlinkedSources = sourceItems
            .Where(s => !s.IsDeleted && !reservedSourceIds.Contains(s.Provenance.ProviderId))
            .ToList();

        var eligibleTargets = destinationItems
            .Where(t => !t.IsDeleted && !reservedDestinationIds.Contains(t.Provenance.ProviderId))
            .ToList();

        var candidatesBySourceId = ItemMatcher.BuildDuplicateCandidateMap(unlinkedSources, eligibleTargets);

        // Identify targets that have exactly one candidate source pointing at them, then
        // count how many source items share that same target (competition detection).
        var competitionForTarget = candidatesBySourceId
            .Where(kv => kv.Value.Length == 1)
            .GroupBy(kv => kv.Value[0].Provenance.ProviderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var item in unlinkedSources)
        {
            // A previous iteration may have reserved this source.
            if (reservedSourceIds.Contains(item.Provenance.ProviderId))
            {
                continue;
            }

            var candidates = candidatesBySourceId[item.Provenance.ProviderId];

            if (candidates.Length == 0)
            {
                links.Add(new Link<TItem>
                {
                    Kind = LinkKind.Unmatched,
                    SourceItem = item,
                    DestinationItem = null,
                    PersistedState = null,
                });
                reservedSourceIds.Add(item.Provenance.ProviderId);
                logger.LogDebug("Unmatched source item: {SourceItemDetails}", item.ToString());
                continue;
            }

            if (candidates.Length > 1)
            {
                logger.LogWarning(
                    "Item {SourceId} matches {MatchCount} contacts on destination side; auto-linking is ambiguous",
                    item.Provenance.ProviderId,
                    candidates.Length);

                links.Add(new Link<TItem>
                {
                    Kind = LinkKind.Ambiguous,
                    SourceItem = item,
                    DestinationItem = null,
                    PersistedState = null,
                });
                continue;
            }

            // Exactly one candidate.
            var target = candidates[0];

            if (reservedDestinationIds.Contains(target.Provenance.ProviderId))
            {
                // Target was claimed by a previous iteration — treat as zero candidates.
                links.Add(new Link<TItem>
                {
                    Kind = LinkKind.Unmatched,
                    SourceItem = item,
                    DestinationItem = null,
                    PersistedState = null,
                });
                reservedSourceIds.Add(item.Provenance.ProviderId);
                logger.LogDebug("Source item {SourceId} candidate target {TargetId} was already reserved; keeping unmatched", item.Provenance.ProviderId, target.Provenance.ProviderId);
                continue;
            }

            if (competitionForTarget.GetValueOrDefault(target.Provenance.ProviderId, 0) > 1)
            {
                logger.LogWarning(
                    "Item {SourceId} matches target {TargetId}, but that target also matches other source items; auto-linking is ambiguous",
                    item.Provenance.ProviderId,
                    target.Provenance.ProviderId);

                links.Add(new Link<TItem>
                {
                    Kind = LinkKind.Ambiguous,
                    SourceItem = item,
                    DestinationItem = target,
                    PersistedState = null,
                });
                continue;
            }

            // Clean 1:1 match — inferred link.
            links.Add(new Link<TItem>
            {
                Kind = LinkKind.Inferred,
                SourceItem = item,
                DestinationItem = target,
                PersistedState = null,
            });
            reservedSourceIds.Add(item.Provenance.ProviderId);
            reservedDestinationIds.Add(target.Provenance.ProviderId);
            logger.LogDebug(
                "Inferred link: source={SourceItemDetails}, destination={DestinationItemDetails}",
                item.ToString(),
                target.ToString());
        }

        // -----------------------------------------------------------------------
        // Pass 3: Remaining unlinked destination items
        // -----------------------------------------------------------------------
        foreach (var dest in destinationItems)
        {
            if (dest.IsDeleted || reservedDestinationIds.Contains(dest.Provenance.ProviderId))
            {
                continue;
            }

            // A target that multiple source items each uniquely pointed at is "contested":
            // ambiguous from the destination's perspective just as the sources were from theirs.
            bool isContested = competitionForTarget.GetValueOrDefault(dest.Provenance.ProviderId, 0) > 1;
            links.Add(new Link<TItem>
            {
                Kind = isContested ? LinkKind.Ambiguous : LinkKind.Unmatched,
                SourceItem = null,
                DestinationItem = dest,
                PersistedState = null,
            });
            logger.LogDebug(
                isContested
                    ? "Contested destination item without safe source match: {DestinationItemDetails}"
                    : "Unmatched destination item: {DestinationItemDetails}",
                dest.ToString());
        }

        return links;
    }
}
