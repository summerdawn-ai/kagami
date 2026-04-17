
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;
/// <summary>
/// Derives sync actions from current job config, observed remote state, and link state.
/// Does not store or depend on mutable job policy per item row.
/// </summary>
public sealed class Planner(ILogger<Planner> logger)
{
    private static readonly ContactMatchComparer ContactMatchComparer = new();

    /// <summary>
    /// Computes the full set of sync actions from current snapshots of both sides and existing links.
    /// </summary>
    /// <remarks>
    /// Planning is performed in three passes:
    /// <list type="number">
    ///   <item>Linked pairs are evaluated first; their endpoints are reserved so they can never
    ///   appear as duplicate-match candidates.</item>
    ///   <item>Unlinked items on source are matched against unreserved destination items.</item>
    ///   <item>Unlinked items on destination are matched against unreserved source items.</item>
    /// </list>
    /// The returned list never contains two actions that share the same source item or the same
    /// target item.
    /// </remarks>
    /// <param name="jobOptions">The job configuration.</param>
    /// <param name="sourceItems">Current items observed on source.</param>
    /// <param name="destinationItems">Current items observed on destination.</param>
    /// <param name="existingLinks">Current link state rows for this job.</param>
    /// <param name="force">
    /// When <c>true</c>, the HasChanged short-circuit is bypassed and all in-scope linked items
    /// are re-evaluated regardless of whether their version/hash has changed.
    /// </param>
    public IReadOnlyList<SyncAction> PlanActions(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sourceItems,
        IReadOnlyList<CanonicalItem> destinationItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        bool force = false)
    {
        var actions = new List<SyncAction>();

        var sourceItemsById = sourceItems.ToDictionary(i => i.SourceId, StringComparer.Ordinal);
        var destinationItemsById = destinationItems.ToDictionary(i => i.SourceId, StringComparer.Ordinal);

        var linkedSourceIds = existingLinks
            .Select(l => l.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var linkedDestinationIds = existingLinks
            .Where(l => l.DestinationId != null)
            .Select(l => l.DestinationId!)
            .ToHashSet(StringComparer.Ordinal);

        // Reservation sets prevent two actions from sharing the same source or target item.
        var reservedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var reservedDestinationIds = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: evaluate existing linked pairs. Linked endpoints are always reserved so that
        // they can never be matched as duplicate candidates in subsequent passes.
        foreach (var link in existingLinks)
        {
            sourceItemsById.TryGetValue(link.SourceId, out var currentSourceItem);
            var currentDestinationItem = link.DestinationId != null ? destinationItemsById.GetValueOrDefault(link.DestinationId) : null;

            reservedSourceIds.Add(link.SourceId);
            if (link.DestinationId != null)
            {
                reservedDestinationIds.Add(link.DestinationId);
            }

            var action = EvaluateLinkedPair(link, currentSourceItem, currentDestinationItem, jobOptions, force);
            if (action != null)
            {
                actions.Add(action);
            }
        }

        // Pass 2: unlinked source items → target destination (only when sync mode allows source→destination).
        if (jobOptions.SyncMode != SyncMode.Reverse)
        {
            PlanUnlinkedItems(
                jobOptions,
                sourceItems,
                destinationItems,
                SyncSide.Destination,
                linkedSourceIds,
                linkedDestinationIds,
                reservedSourceIds,
                reservedDestinationIds,
                actions);
        }

        // Pass 3: unlinked destination items → target source (only when sync mode allows destination→source).
        if (jobOptions.SyncMode != SyncMode.Forward)
        {
            PlanUnlinkedItems(
                jobOptions,
                destinationItems,
                sourceItems,
                SyncSide.Source,
                linkedDestinationIds,
                linkedSourceIds,
                reservedDestinationIds,
                reservedSourceIds,
                actions);
        }

        return actions;
    }

    private static SyncAction? EvaluateLinkedPair(
        LinkStateRow link,
        CanonicalItem? currentSourceItem,
        CanonicalItem? currentDestinationItem,
        JobOptions jobOptions,
        bool force)
    {
        // Deletion on source takes priority.
        if (currentSourceItem?.IsDeleted == true)
        {
            if (jobOptions.SyncMode != SyncMode.Reverse
                && jobOptions.DeletePolicy != DeletePolicy.Ignore
                && link.DestinationId != null)
            {
                return new SyncAction
                {
                    Kind = SyncActionKind.Delete,
                    TargetSide = SyncSide.Destination,
                    DeleteId = link.DestinationId,
                    Reason = "Source item deleted",
                };
            }

            return null;
        }

        // Deletion on destination.
        if (currentDestinationItem?.IsDeleted == true)
        {
            if (jobOptions.SyncMode != SyncMode.Forward && jobOptions.DeletePolicy != DeletePolicy.Ignore)
            {
                return new SyncAction
                {
                    Kind = SyncActionKind.Delete,
                    TargetSide = SyncSide.Source,
                    DeleteId = link.SourceId,
                    Reason = "Source item deleted",
                };
            }

            return null;
        }

        bool sourceChanged = currentSourceItem != null && (force || HasChanged(currentSourceItem, link, SyncSide.Source));
        bool destinationChanged = currentDestinationItem != null && (force || HasChanged(currentDestinationItem, link, SyncSide.Destination));

        return jobOptions.SyncMode switch
        {
            SyncMode.Forward when !sourceChanged => null,
            SyncMode.Forward when destinationChanged => ResolveConflict(currentSourceItem!, currentDestinationItem, SyncSide.Source, SyncSide.Destination, jobOptions),
            SyncMode.Forward => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.Destination,
                Item = currentSourceItem,
                Reason = "Item changed on source side",
            },

            SyncMode.Reverse when !destinationChanged => null,
            SyncMode.Reverse when sourceChanged => ResolveConflict(currentDestinationItem!, currentSourceItem, SyncSide.Destination, SyncSide.Source, jobOptions),
            SyncMode.Reverse => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.Source,
                Item = currentDestinationItem,
                Reason = "Item changed on source side",
            },

            // Bidirectional
            _ when !sourceChanged && !destinationChanged => null,
            _ when sourceChanged && !destinationChanged => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.Destination,
                Item = currentSourceItem,
                Reason = "Item changed on source side",
            },
            _ when !sourceChanged => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.Source,
                Item = currentDestinationItem,
                Reason = "Item changed on source side",
            },
            // Both changed: resolve using conflict policy. Prefer the A-originating direction
            // as the primary so that SourceWins and LastWriteWins work naturally; DestinationWins is
            // handled by checking the policy explicitly.
            _ when jobOptions.ConflictPolicy == ConflictPolicy.DestinationWins && currentDestinationItem != null => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.Source,
                Item = currentDestinationItem,
                Reason = "Conflict: destination wins per policy",
            },
            _ => ResolveConflict(currentSourceItem!, currentDestinationItem, SyncSide.Source, SyncSide.Destination, jobOptions),
        };
    }

    /// <summary>
    /// Plans actions for unlinked source items against the eligible pool of target items.
    /// </summary>
    /// <param name="jobOptions">Job configuration.</param>
    /// <param name="sourceItems">All items on the source side.</param>
    /// <param name="targetItems">All items on the target side.</param>
    /// <param name="targetSide">Which side is the target.</param>
    /// <param name="linkedSourceIds">Source-side ids that are already part of an existing link.</param>
    /// <param name="linkedTargetIds">Target-side ids that are already part of an existing link.</param>
    /// <param name="reservedSourceIds">
    /// Source ids already claimed by a planned action; updated in place as new actions are added.
    /// </param>
    /// <param name="reservedTargetIds">
    /// Target ids already claimed by a planned action; updated in place as new actions are added.
    /// </param>
    /// <param name="actions">Accumulator list for planned actions.</param>
    private void PlanUnlinkedItems(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sourceItems,
        IReadOnlyList<CanonicalItem> targetItems,
        SyncSide targetSide,
        IReadOnlySet<string> linkedSourceIds,
        IReadOnlySet<string> linkedTargetIds,
        HashSet<string> reservedSourceIds,
        HashSet<string> reservedTargetIds,
        List<SyncAction> actions)
    {
        // Eligible target pool: unlinked, unreserved, not deleted.
        var eligibleTargets = targetItems
            .Where(t => !t.IsDeleted && !linkedTargetIds.Contains(t.SourceId) && !reservedTargetIds.Contains(t.SourceId))
            .ToList();

        var unlinkedSources = sourceItems
            .Where(s => !s.IsDeleted && !linkedSourceIds.Contains(s.SourceId) && !reservedSourceIds.Contains(s.SourceId))
            .ToList();

        // Build a candidate map: for each source item, which eligible target items match it?
        var candidatesBySourceId = new Dictionary<string, CanonicalItem[]>(StringComparer.Ordinal);
        foreach (var item in unlinkedSources)
        {
            candidatesBySourceId[item.SourceId] = FindDuplicateMatches(item, eligibleTargets);
        }

        // For each eligible target that appears as the unique candidate for exactly one source,
        // count how many sources point to it. If > 1 the match is ambiguous (many-to-one).
        var competitionForTarget = candidatesBySourceId
            .Where(kv => kv.Value.Length == 1)
            .GroupBy(kv => kv.Value[0].SourceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var item in unlinkedSources)
        {
            // Re-check: a previous iteration in this pass may have reserved this source.
            if (reservedSourceIds.Contains(item.SourceId))
            {
                continue;
            }

            var candidates = candidatesBySourceId[item.SourceId];

            if (candidates.Length == 0)
            {
                actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Create,
                    TargetSide = targetSide,
                    Item = item,
                    Reason = "New item on source side",
                });
                reservedSourceIds.Add(item.SourceId);
                continue;
            }

            if (candidates.Length > 1)
            {
                logger.LogWarning(
                    "Item {SourceId} has {MatchCount} matching contacts on side {TargetSide}; skipping auto-linking",
                    item.SourceId,
                    candidates.Length,
                    targetSide);

                actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.NoOp,
                    TargetSide = targetSide,
                    Item = item,
                    Reason = "Multiple matching contacts on target side",
                });
                continue;
            }

            // Exactly one candidate.
            var target = candidates[0];

            if (reservedTargetIds.Contains(target.SourceId))
            {
                // The target was claimed by a previous action after the eligible pool was built.
                // The linked endpoint is unavailable as a candidate, so treat as zero matches → create.
                actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Create,
                    TargetSide = targetSide,
                    Item = item,
                    Reason = "New item on source side",
                });
                reservedSourceIds.Add(item.SourceId);
                continue;
            }

            if (competitionForTarget.GetValueOrDefault(target.SourceId, 0) > 1)
            {
                // Multiple source items compete for this target → ambiguous, skip.
                logger.LogWarning(
                    "Item {SourceId} matches target contact {TargetId}, but that target also matches other source contacts on side {TargetSide}; skipping auto-linking",
                    item.SourceId,
                    target.SourceId,
                    targetSide);

                actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.NoOp,
                    TargetSide = targetSide,
                    Item = item,
                    Reason = "Target contact matches multiple source contacts",
                });
                continue;
            }

            // Clean 1:1 match.
            actions.Add(new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = item,
                MatchedTargetItem = target,
                Reason = "Matched existing contact on target side",
            });
            reservedSourceIds.Add(item.SourceId);
            reservedTargetIds.Add(target.SourceId);
        }
    }

    private static bool HasChanged(CanonicalItem item, LinkStateRow link, SyncSide side)
    {
        if (side == SyncSide.Source)
        {
            if (item.Version != null && link.SourceVersion != null)
            {
                return item.Version != link.SourceVersion;
            }

            if (item.ContentHash != null && link.SourceHash != null)
            {
                return item.ContentHash != link.SourceHash;
            }
        }
        else
        {
            if (item.Version != null && link.DestinationVersion != null)
            {
                return item.Version != link.DestinationVersion;
            }

            if (item.ContentHash != null && link.DestinationHash != null)
            {
                return item.ContentHash != link.DestinationHash;
            }
        }

        // Without version/hash info, assume changed.
        return true;
    }

    private static SyncAction ResolveConflict(
        CanonicalItem sourceItem,
        CanonicalItem? targetItem,
        SyncSide sourceSide,
        SyncSide targetSide,
        JobOptions jobOptions) =>
        jobOptions.ConflictPolicy switch
        {
            ConflictPolicy.SourceWins when sourceSide == SyncSide.Source => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: source wins per policy",
            },
            ConflictPolicy.SourceWins => new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: source wins per policy",
            },
            ConflictPolicy.DestinationWins when sourceSide == SyncSide.Destination => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            ConflictPolicy.DestinationWins => new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            ConflictPolicy.Skip => new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: skipped per policy",
            },
            _ => ResolveLastWriteWinsConflict(sourceItem, targetItem, sourceSide, targetSide),
        };

    private static SyncAction ResolveLastWriteWinsConflict(
        CanonicalItem sourceItem,
        CanonicalItem? targetItem,
        SyncSide sourceSide,
        SyncSide targetSide)
    {
        var sourceLastModified = GetLastModified(sourceItem);
        var targetLastModified = GetLastModified(targetItem);

        bool sourceWins = sourceLastModified > targetLastModified
            || (sourceLastModified == targetLastModified && sourceSide == SyncSide.Source)
            || (sourceLastModified is not null && targetLastModified is null);

        return sourceWins
            ? new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: last write wins per policy",
            }
            : new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: target side wins per last-write-wins policy",
            };
    }

    private static DateTimeOffset? GetLastModified(CanonicalItem? item) =>
        item?.Payload switch
        {
            CanonicalCalendarEvent calendarEvent => calendarEvent.LastModified,
            CanonicalContact contact => contact.LastModified,
            _ => null,
        };

    private static CanonicalItem[] FindDuplicateMatches(
        CanonicalItem item,
        IReadOnlyList<CanonicalItem> eligibleTargetItems)
    {
        if (item.Payload is not CanonicalContact)
        {
            return [];
        }

        return [.. eligibleTargetItems.Where(targetItem => ContactMatchComparer.IsMatch(item, targetItem))];
    }
}
