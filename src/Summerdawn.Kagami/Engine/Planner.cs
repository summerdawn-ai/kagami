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
    /// Computes the actions required to synchronize side-B for new/changed items observed on side A.
    /// </summary>
    /// <param name="jobOptions">The job configuration.</param>
    /// <param name="sideAItems">Items observed on side A.</param>
    /// <param name="currentSideBItems">Current items observed on side B.</param>
    /// <param name="existingLinks">Current link state rows for this job.</param>
    /// <param name="force">
    /// When <c>true</c>, the HasChanged short-circuit is bypassed and all in-scope linked items
    /// are re-evaluated regardless of whether their version/hash has changed.
    /// </param>
    public IReadOnlyList<SyncAction> PlanFromSideA(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sideAItems,
        IReadOnlyList<CanonicalItem> currentSideBItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        bool force = false)
    {
        if (jobOptions.SyncMode == SyncMode.BToA)
        {
            return [];
        }

        return PlanActions(sideAItems, currentSideBItems, existingLinks, SyncSide.A, SyncSide.B, jobOptions, force);
    }

    /// <summary>
    /// Computes the actions required to synchronize side-A for new/changed items observed on side B.
    /// </summary>
    /// <param name="jobOptions">The job configuration.</param>
    /// <param name="sideBItems">Items observed on side B.</param>
    /// <param name="currentSideAItems">Current items observed on side A.</param>
    /// <param name="existingLinks">Current link state rows for this job.</param>
    /// <param name="force">
    /// When <c>true</c>, the HasChanged short-circuit is bypassed and all in-scope linked items
    /// are re-evaluated regardless of whether their version/hash has changed.
    /// </param>
    public IReadOnlyList<SyncAction> PlanFromSideB(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sideBItems,
        IReadOnlyList<CanonicalItem> currentSideAItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        bool force = false)
    {
        if (jobOptions.SyncMode == SyncMode.AToB)
        {
            return [];
        }

        return PlanActions(sideBItems, currentSideAItems, existingLinks, SyncSide.B, SyncSide.A, jobOptions, force);
    }

    private IReadOnlyList<SyncAction> PlanActions(
        IReadOnlyList<CanonicalItem> sourceItems,
        IReadOnlyList<CanonicalItem> currentTargetItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncSide sourceSide,
        SyncSide targetSide,
        JobOptions jobOptions,
        bool force = false)
    {
        var actions = new List<SyncAction>();
        var currentTargetItemsById = currentTargetItems.ToDictionary(item => item.SourceId);
        var linkedTargetIds = existingLinks
            .Select(link => targetSide == SyncSide.B ? link.SideBId : link.SideAId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var linksBySourceId = targetSide == SyncSide.B
            ? existingLinks.ToDictionary(l => l.SideAId)
            : existingLinks.Where(l => l.SideBId != null).ToDictionary(l => l.SideBId!);
        var duplicateMatchesBySourceId = BuildDuplicateMatchesBySourceId(sourceItems, currentTargetItems, linkedTargetIds, linksBySourceId);
        var duplicateTargetMatchCounts = duplicateMatchesBySourceId
            .Where(match => match.Value.Length == 1)
            .GroupBy(match => match.Value[0].SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var item in sourceItems)
        {
            if (item.IsDeleted)
            {
                if (jobOptions.DeletePolicy == DeletePolicy.Ignore)
                {
                    continue;
                }

                if (linksBySourceId.TryGetValue(item.SourceId, out var existingForDelete))
                {
                    string? deleteId = targetSide == SyncSide.B ? existingForDelete.SideBId : existingForDelete.SideAId;
                    if (deleteId != null)
                    {
                        actions.Add(new SyncAction
                        {
                            Kind = SyncActionKind.Delete,
                            TargetSide = targetSide,
                            DeleteId = deleteId,
                            Reason = "Source item deleted",
                        });
                    }
                }

                continue;
            }

            if (!linksBySourceId.TryGetValue(item.SourceId, out var link))
            {
                CanonicalItem[] duplicateMatches = duplicateMatchesBySourceId.GetValueOrDefault(item.SourceId, []);
                if (duplicateMatches.Length == 1)
                {
                    if (duplicateTargetMatchCounts[duplicateMatches[0].SourceId] > 1)
                    {
                        logger.LogWarning(
                            "Item {SourceId} matches target contact {TargetId}, but that target also matches other source contacts on side {TargetSide}; skipping auto-linking",
                            item.SourceId,
                            duplicateMatches[0].SourceId,
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

                    actions.Add(new SyncAction
                    {
                        Kind = SyncActionKind.Update,
                        TargetSide = targetSide,
                        Item = item,
                        MatchedTargetItem = duplicateMatches[0],
                        Reason = "Matched existing contact on target side",
                    });
                    continue;
                }

                if (duplicateMatches.Length > 1)
                {
                    logger.LogWarning(
                        "Item {SourceId} has {MatchCount} matching contacts on side {TargetSide}; skipping auto-linking",
                        item.SourceId,
                        duplicateMatches.Length,
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

                // New item — create on target side
                actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Create,
                    TargetSide = targetSide,
                    Item = item,
                    Reason = "New item on source side",
                });
                continue;
            }

            // Check if item has changed (skip check when force is requested)
            bool changed = force || HasChanged(item, link, sourceSide);
            if (!changed)
            {
                logger.LogDebug("Item {Id} has not changed, skipping", item.SourceId);
                continue;
            }

            // Check for conflict (both sides changed)
            CanonicalItem? currentTargetItem = TryGetCurrentTargetItem(link, targetSide, currentTargetItemsById);
            bool targetChanged = HasTargetChanged(currentTargetItem, link, targetSide);
            if (targetChanged)
            {
                actions.Add(ResolveConflict(item, currentTargetItem, sourceSide, targetSide, jobOptions));
                continue;
            }

            actions.Add(new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = item,
                Reason = "Item changed on source side",
            });
        }

        return actions;
    }

    private static bool HasChanged(CanonicalItem item, LinkStateRow link, SyncSide sourceSide)
    {
        if (sourceSide == SyncSide.A)
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

        // Without version/hash info, assume changed
        return true;
    }

    private static bool HasTargetChanged(CanonicalItem? item, LinkStateRow link, SyncSide targetSide)
    {
        return item is not null && HasChanged(item, link, targetSide);
    }

    private static CanonicalItem? TryGetCurrentTargetItem(
        LinkStateRow link,
        SyncSide targetSide,
        IReadOnlyDictionary<string, CanonicalItem> currentTargetItemsById)
    {
        string? targetId = targetSide == SyncSide.B ? link.SideBId : link.SideAId;
        return targetId is not null && currentTargetItemsById.TryGetValue(targetId, out CanonicalItem? item) ? item : null;
    }

    private static SyncAction ResolveConflict(
        CanonicalItem sourceItem,
        CanonicalItem? targetItem,
        SyncSide sourceSide,
        SyncSide targetSide,
        JobOptions jobOptions)
    {
        return jobOptions.ConflictPolicy switch
        {
            ConflictPolicy.SideAWins when targetSide == SyncSide.B => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = sourceItem,
                Reason = "Conflict: side A wins per policy",
            },
            ConflictPolicy.SideBWins when targetSide == SyncSide.A => new SyncAction
            {
                Kind = SyncActionKind.Update,
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
    }

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
        IReadOnlyList<CanonicalItem> currentTargetItems,
        IReadOnlySet<string> linkedTargetIds)
    {
        if (item.Payload is not CanonicalContact)
        {
            return [];
        }

        return [.. currentTargetItems
            .Where(targetItem => !targetItem.IsDeleted)
            .Where(targetItem => !linkedTargetIds.Contains(targetItem.SourceId))
            .Where(targetItem => ContactMatchComparer.IsMatch(item, targetItem))];
    }

    private static Dictionary<string, CanonicalItem[]> BuildDuplicateMatchesBySourceId(
        IReadOnlyList<CanonicalItem> sourceItems,
        IReadOnlyList<CanonicalItem> currentTargetItems,
        IReadOnlySet<string> linkedTargetIds,
        IReadOnlyDictionary<string, LinkStateRow> linksBySourceId)
    {
        Dictionary<string, CanonicalItem[]> matches = new(StringComparer.Ordinal);

        foreach (CanonicalItem item in sourceItems)
        {
            if (item.IsDeleted || linksBySourceId.ContainsKey(item.SourceId))
            {
                continue;
            }

            CanonicalItem[] duplicateMatches = FindDuplicateMatches(item, currentTargetItems, linkedTargetIds);
            if (duplicateMatches.Length > 0)
            {
                matches[item.SourceId] = duplicateMatches;
            }
        }

        return matches;
    }
}
