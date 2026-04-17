
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

            // Both sides are absent — no action is possible regardless of policy or --force.
            // Still reserved above so neither endpoint leaks into the unlinked-item passes.
            bool sourcePresent = currentSourceItem != null && !currentSourceItem.IsDeleted;
            bool destinationPresent = currentDestinationItem != null && !currentDestinationItem.IsDeleted;
            if (!sourcePresent && !destinationPresent)
            {
                continue;
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

    /// <summary>
    /// Evaluates a known-linked pair and returns the action to take, or <c>null</c> if no action is needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An item that is absent from the current filtered scan (<c>null</c>) is treated identically to
    /// one with <c>IsDeleted = true</c>.  This covers both hard remote deletions and items that have
    /// drifted out of the active filter scope (e.g. a contact that moved from category X to category Y).
    /// </para>
    /// <para>
    /// The method is only called when at least one side is present; the caller short-circuits when both
    /// sides are absent.
    /// </para>
    /// </remarks>
    private static SyncAction? EvaluateLinkedPair(
        LinkStateRow link,
        CanonicalItem? currentSourceItem,
        CanonicalItem? currentDestinationItem,
        JobOptions jobOptions,
        bool force)
    {
        // An item absent from the full filtered scan (null) is treated the same as IsDeleted=true
        // within the scope of this filter/job. This covers both true remote deletions and items that
        // have drifted out of the active filter (e.g. category X→Y).
        bool sourceIsGone = currentSourceItem == null || currentSourceItem.IsDeleted;
        bool destinationIsGone = link.DestinationId != null
            && (currentDestinationItem == null || currentDestinationItem.IsDeleted);

        // --- Source gone ---
        if (sourceIsGone)
        {
            if (force)
            {
                // --force = "act as if DB is empty". Source is absent, so there is nothing to push.
                // Returning null (no action) is intentional: unlike the non-force suppression paths
                // that return NoOp to keep an audit trail, here the caller is explicitly opting out of
                // state-based reasoning. A subsequent non-force run will re-evaluate once remote state
                // is clearer.
                return null;
            }

            if (jobOptions.SyncMode != SyncMode.Reverse
                && jobOptions.DeletePolicy != DeletePolicy.Ignore
                && link.DestinationId != null
                && !destinationIsGone)
            {
                return new SyncAction
                {
                    Kind = SyncActionKind.Delete,
                    TargetSide = SyncSide.Destination,
                    Item = currentDestinationItem,
                    DeleteId = link.DestinationId,
                    Reason = "Source item absent (deleted or out of filter scope)",
                };
            }

            string sourceNoOpReason = jobOptions.DeletePolicy == DeletePolicy.Ignore
                ? "Source item absent but delete policy is Ignore"
                : "Source item absent but sync direction does not propagate source-side deletions";

            return new SyncAction
            {
                Kind = SyncActionKind.Skip,
                TargetSide = SyncSide.Destination,
                Item = currentDestinationItem,
                Reason = sourceNoOpReason,
            };
        }

        // --- Destination gone ---
        if (destinationIsGone)
        {
            if (force && jobOptions.SyncMode != SyncMode.Reverse)
            {
                // --force: act as if DB is empty. Source is present but destination is gone.
                // On first run with empty DB we would Create on destination — do the same here
                // instead of trying to Update a missing item (→ 404).
                return new SyncAction
                {
                    Kind = SyncActionKind.Create,
                    TargetSide = SyncSide.Destination,
                    Item = currentSourceItem,
                    Reason = "Destination absent; recreating per --force",
                };
            }

            if (jobOptions.SyncMode != SyncMode.Forward
                && jobOptions.DeletePolicy != DeletePolicy.Ignore)
            {
                return new SyncAction
                {
                    Kind = SyncActionKind.Delete,
                    TargetSide = SyncSide.Source,
                    Item = currentSourceItem,
                    DeleteId = link.SourceId,
                    Reason = "Destination item absent (deleted or out of filter scope)",
                };
            }

            string destinationNoOpReason = jobOptions.SyncMode == SyncMode.Forward
                ? "Destination item absent but sync direction does not propagate destination-side deletions"
                : "Destination item absent but delete policy is Ignore";

            return new SyncAction
            {
                Kind = SyncActionKind.Skip,
                TargetSide = SyncSide.Source,
                Item = currentSourceItem,
                Reason = destinationNoOpReason,
            };
        }

        // Both items are present and non-deleted from this point on.
        bool sourceChanged = force || HasChanged(currentSourceItem!, link, SyncSide.Source);
        bool destinationChanged = force || HasChanged(currentDestinationItem!, link, SyncSide.Destination);

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
                Reason = "Item changed on destination side",
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
                Reason = "Item changed on destination side",
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
                    Kind = SyncActionKind.Skip,
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
                    Kind = SyncActionKind.Skip,
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
                Kind = SyncActionKind.Skip,
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
                Kind = SyncActionKind.Skip,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            ConflictPolicy.Skip => new SyncAction
            {
                Kind = SyncActionKind.Skip,
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
                Kind = SyncActionKind.Skip,
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
