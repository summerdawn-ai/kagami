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
    /// <summary>
    /// Computes the actions required to synchronize side-B for new/changed items observed on side A.
    /// </summary>
    public IReadOnlyList<SyncAction> PlanFromSideA(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sideAItems,
        IReadOnlyList<LinkStateRow> existingLinks)
    {
        if (jobOptions.SyncMode == SyncMode.BToA)
        {
            return [];
        }

        return PlanActions(sideAItems, existingLinks, SyncSide.B, jobOptions);
    }

    /// <summary>
    /// Computes the actions required to synchronize side-A for new/changed items observed on side B.
    /// </summary>
    public IReadOnlyList<SyncAction> PlanFromSideB(
        JobOptions jobOptions,
        IReadOnlyList<CanonicalItem> sideBItems,
        IReadOnlyList<LinkStateRow> existingLinks)
    {
        if (jobOptions.SyncMode == SyncMode.AToB)
        {
            return [];
        }

        return PlanActions(sideBItems, existingLinks, SyncSide.A, jobOptions);
    }

    private IReadOnlyList<SyncAction> PlanActions(
        IReadOnlyList<CanonicalItem> sourceItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncSide targetSide,
        JobOptions jobOptions)
    {
        var actions = new List<SyncAction>();
        var linksBySourceId = targetSide == SyncSide.B
            ? existingLinks.ToDictionary(l => l.SideAId)
            : existingLinks.Where(l => l.SideBId != null).ToDictionary(l => l.SideBId!);

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

            // Check if item has changed
            bool changed = HasChanged(item, link, targetSide == SyncSide.B ? SyncSide.A : SyncSide.B);
            if (!changed)
            {
                logger.LogDebug("Item {Id} has not changed, skipping", item.SourceId);
                continue;
            }

            // Check for conflict (both sides changed)
            bool targetChanged = HasTargetChanged(link, targetSide);
            if (targetChanged)
            {
                actions.Add(ResolveConflict(item, link, targetSide, jobOptions));
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

    private static bool HasTargetChanged(LinkStateRow link, SyncSide targetSide)
    {
        // A simplified check — in production this would compare versions
        // against what was known when last synced
        _ = link;
        _ = targetSide;
        return false;
    }

    private static SyncAction ResolveConflict(CanonicalItem item, LinkStateRow link, SyncSide targetSide, JobOptions jobOptions)
    {
        _ = link;
        return jobOptions.ConflictPolicy switch
        {
            ConflictPolicy.SideAWins when targetSide == SyncSide.B => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = item,
                Reason = "Conflict: side A wins per policy",
            },
            ConflictPolicy.SideBWins when targetSide == SyncSide.A => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = item,
                Reason = "Conflict: side B wins per policy",
            },
            ConflictPolicy.Skip => new SyncAction
            {
                Kind = SyncActionKind.NoOp,
                TargetSide = targetSide,
                Item = item,
                Reason = "Conflict: skipped per policy",
            },
            _ => new SyncAction
            {
                Kind = SyncActionKind.Update,
                TargetSide = targetSide,
                Item = item,
                Reason = "Conflict: last write wins per policy",
            },
        };
    }
}
