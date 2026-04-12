namespace Summerdawn.Kagami.Engine;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

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
    ///   <item>Unlinked items on side A are matched against unreserved side-B items.</item>
    ///   <item>Unlinked items on side B are matched against unreserved side-A items.</item>
    /// </list>
    /// The returned list never contains two actions that share the same source item or the same
    /// target item.
    /// </remarks>
    /// <param name="jobOptions">The job configuration.</param>
    /// <param name="sideAItems">Current items observed on side A.</param>
    /// <param name="sideBItems">Current items observed on side B.</param>
    /// <param name="existingLinks">Current link state rows for this job.</param>
    /// <param name="force">
    /// When <c>true</c>, the HasChanged short-circuit is bypassed and all in-scope linked items
    /// are re-evaluated regardless of whether their version/hash has changed.
    /// </param>
    public IReadOnlyList<SyncAction> PlanActions(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sideAItems,
        IReadOnlyList<CanonicalItem> sideBItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        bool force = false)
    {
        var actions = new List<SyncAction>();

        var itemsAById = sideAItems.ToDictionary(i => i.SourceId, StringComparer.Ordinal);
        var itemsBById = sideBItems.ToDictionary(i => i.SourceId, StringComparer.Ordinal);

        var linkedAIds = existingLinks
            .Select(l => l.SideAId)
            .ToHashSet(StringComparer.Ordinal);
        var linkedBIds = existingLinks
            .Where(l => l.SideBId != null)
            .Select(l => l.SideBId!)
            .ToHashSet(StringComparer.Ordinal);

        // Reservation sets prevent two actions from sharing the same source or target item.
        var reservedAIds = new HashSet<string>(StringComparer.Ordinal);
        var reservedBIds = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: evaluate existing linked pairs. Linked endpoints are always reserved so that
        // they can never be matched as duplicate candidates in subsequent passes.
        foreach (LinkStateRow link in existingLinks)
        {
            itemsAById.TryGetValue(link.SideAId, out CanonicalItem? currentA);
            CanonicalItem? currentB = link.SideBId != null ? itemsBById.GetValueOrDefault(link.SideBId) : null;

            reservedAIds.Add(link.SideAId);
            if (link.SideBId != null)
            {
                reservedBIds.Add(link.SideBId);
            }

            SyncAction? action = EvaluateLinkedPair(link, currentA, currentB, jobOptions, force);
            if (action != null)
            {
                actions.Add(action);
            }
        }

        // Pass 2: unlinked A items → target B (only when sync mode allows A→B).
        if (jobOptions.SyncMode != SyncMode.BToA)
        {
            PlanUnlinkedItems(
                jobOptions,
                sideAItems,
                sideBItems,
                SyncSide.B,
                linkedAIds,
                linkedBIds,
                reservedAIds,
                reservedBIds,
                actions);
        }

        // Pass 3: unlinked B items → target A (only when sync mode allows B→A).
        if (jobOptions.SyncMode != SyncMode.AToB)
        {
            PlanUnlinkedItems(
                jobOptions,
                sideBItems,
                sideAItems,
                SyncSide.A,
                linkedBIds,
                linkedAIds,
                reservedBIds,
                reservedAIds,
                actions);
        }

        return actions;
    }

    private static SyncAction? EvaluateLinkedPair(
        LinkStateRow link,
        CanonicalItem? currentA,
        CanonicalItem? currentB,
        JobOptions jobOptions,
        bool force)
    {
        // Deletion on side A takes priority.
        if (currentA?.IsDeleted == true)
        {
            if (jobOptions.SyncMode != SyncMode.BToA
                && jobOptions.DeletePolicy != DeletePolicy.Ignore
                && link.SideBId != null)
            {
                return new SyncAction
                {
                    Kind = SyncActionKind.Delete,
                    TargetSide = SyncSide.B,
                    DeleteId = link.SideBId,
                    Reason = "Source item deleted",
                };
            }

            return null;
        }

        // Deletion on side B.
        if (currentB?.IsDeleted == true)
        {
            if (jobOptions.SyncMode != SyncMode.AToB && jobOptions.DeletePolicy != DeletePolicy.Ignore)
            {
                return new SyncAction
                {
                    Kind = SyncActionKind.Delete,
                    TargetSide = SyncSide.A,
                    DeleteId = link.SideAId,
                    Reason = "Source item deleted",
                };
            }

            return null;
        }

        bool aChanged = currentA != null && (force || HasChanged(currentA, link, SyncSide.A));
        bool bChanged = currentB != null && (force || HasChanged(currentB, link, SyncSide.B));

        return jobOptions.SyncMode switch
        {
            SyncMode.AToB when !aChanged => null,
            SyncMode.AToB when bChanged => ResolveConflict(currentA!, currentB, SyncSide.A, SyncSide.B, jobOptions),
            SyncMode.AToB => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.B,
                Item = currentA,
                Reason = "Item changed on source side",
            },

            SyncMode.BToA when !bChanged => null,
            SyncMode.BToA when aChanged => ResolveConflict(currentB!, currentA, SyncSide.B, SyncSide.A, jobOptions),
            SyncMode.BToA => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.A,
                Item = currentB,
                Reason = "Item changed on source side",
            },

            // Bidirectional
            _ when !aChanged && !bChanged => null,
            _ when aChanged && !bChanged => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.B,
                Item = currentA,
                Reason = "Item changed on source side",
            },
            _ when !aChanged => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.A,
                Item = currentB,
                Reason = "Item changed on source side",
            },
            // Both changed: resolve using conflict policy. Prefer the A-originating direction
            // as the primary so that SideAWins and LastWriteWins work naturally; SideBWins is
            // handled by checking the policy explicitly.
            _ when jobOptions.ConflictPolicy == ConflictPolicy.SideBWins && currentB != null => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = SyncSide.A,
                Item = currentB,
                Reason = "Conflict: side B wins per policy",
            },
            _ => ResolveConflict(currentA!, currentB, SyncSide.A, SyncSide.B, jobOptions),
        };
    }

    /// <summary>
    /// Plans actions for unlinked source items against the eligible pool of target items.
    /// </summary>
    /// <param name="jobOptions">Job configuration.</param>
    /// <param name="sourceItems">All items on the source side.</param>
    /// <param name="targetItems">All items on the target side.</param>
    /// <param name="targetSide">Which side is the target.</param>
    /// <param name="linkedSourceIds">Source-side IDs that are already part of an existing link.</param>
    /// <param name="linkedTargetIds">Target-side IDs that are already part of an existing link.</param>
    /// <param name="reservedSourceIds">
    /// Source IDs already claimed by a planned action; updated in place as new actions are added.
    /// </param>
    /// <param name="reservedTargetIds">
    /// Target IDs already claimed by a planned action; updated in place as new actions are added.
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
        foreach (CanonicalItem item in unlinkedSources)
        {
            candidatesBySourceId[item.SourceId] = FindDuplicateMatches(item, eligibleTargets);
        }

        // For each eligible target that appears as the unique candidate for exactly one source,
        // count how many sources point to it. If > 1 the match is ambiguous (many-to-one).
        var competitionForTarget = candidatesBySourceId
            .Where(kv => kv.Value.Length == 1)
            .GroupBy(kv => kv.Value[0].SourceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (CanonicalItem item in unlinkedSources)
        {
            // Re-check: a previous iteration in this pass may have reserved this source.
            if (reservedSourceIds.Contains(item.SourceId))
            {
                continue;
            }

            CanonicalItem[] candidates = candidatesBySourceId[item.SourceId];

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
            CanonicalItem target = candidates[0];

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
        if (side == SyncSide.A)
        {
            if (item.Version != null && link.SideAVersion != null)
            {
                return item.Version != link.SideAVersion;
            }

            if (item.ContentHash != null && link.SideAHash != null)
            {
                return item.ContentHash != link.SideAHash;
            }
        }
        else
        {
            if (item.Version != null && link.SideBVersion != null)
            {
                return item.Version != link.SideBVersion;
            }

            if (item.ContentHash != null && link.SideBHash != null)
            {
                return item.ContentHash != link.SideBHash;
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
            ConflictPolicy.SideAWins when sourceSide == SyncSide.A => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: side A wins per policy",
            },
            ConflictPolicy.SideAWins => new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: side A wins per policy",
            },
            ConflictPolicy.SideBWins when sourceSide == SyncSide.B => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: side B wins per policy",
            },
            ConflictPolicy.SideBWins => new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: side B wins per policy",
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
        DateTimeOffset? sourceLastModified = GetLastModified(sourceItem);
        DateTimeOffset? targetLastModified = GetLastModified(targetItem);

        bool sourceWins = sourceLastModified > targetLastModified
            || (sourceLastModified == targetLastModified && sourceSide == SyncSide.A)
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

        return [.. eligibleTargetItems
            .Where(targetItem => !targetItem.IsDeleted)
            .Where(targetItem => ContactMatchComparer.IsMatch(item, targetItem))];
    }
}
