using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Configuration.ConflictPolicy;
using static Summerdawn.Kagami.Configuration.DeletePolicy;
using static Summerdawn.Kagami.Configuration.SyncMode;
using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Derives sync actions from current job config, observed remote state, and link state.
/// Does not store or depend on mutable job policy per item row.
/// </summary>
public sealed class Planner(ILogger<Planner> logger)
{
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
    /// <remarks>
    /// <see cref="JobOptions.Force"/> bypasses both HasChanged and the content-sameness check,
    /// causing every in-scope item to be written unconditionally.
    /// <see cref="JobOptions.Full"/> only affects cursor behavior in the executor; the planner
    /// treats it identically to a normal run.
    /// </remarks>
    public IReadOnlyList<SyncAction<TItem>> PlanActions<TItem>(
        JobOptions jobOptions,
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> destinationItems,
        IReadOnlyList<LinkStateRow> existingLinks) where TItem : CanonicalItem
    {
        var actions = new List<SyncAction<TItem>>();

        var sourceItemsById = sourceItems.ToDictionary(i => i.Provenance.ProviderId, StringComparer.Ordinal);
        var destinationItemsById = destinationItems.ToDictionary(i => i.Provenance.ProviderId, StringComparer.Ordinal);

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

            var action = EvaluateLinkedPair(link, currentSourceItem, currentDestinationItem, jobOptions);
            if (action != null)
            {
                actions.Add(action);
            }
        }

        // Pass 2: unlinked source items → target destination (only when sync mode allows source→destination).
        if (jobOptions.SyncMode != Reverse)
        {
            PlanUnlinkedItems(
                jobOptions,
                sourceItems,
                destinationItems,
                SourceToDestination,
                linkedSourceIds,
                linkedDestinationIds,
                reservedSourceIds,
                reservedDestinationIds,
                actions);
        }

        // Pass 3: unlinked destination items → target source (only when sync mode allows destination→source).
        if (jobOptions.SyncMode != Forward)
        {
            PlanUnlinkedItems(
                jobOptions,
                destinationItems,
                sourceItems,
                DestinationToSource,
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
    private static SyncAction<TItem>? EvaluateLinkedPair<TItem>(
        LinkStateRow link,
        TItem? currentSourceItem,
        TItem? currentDestinationItem,
        JobOptions jobOptions) where TItem : CanonicalItem
    {
        bool force = jobOptions.Force;
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
                // that return Skip to keep an audit trail, here the caller is explicitly opting out of
                // state-based reasoning. A subsequent non-force run will re-evaluate once remote state
                // is clearer.
                return null;
            }

            if (jobOptions.SyncMode != Reverse
                && jobOptions.DeletePolicy != Ignore
                && link.DestinationId != null
                && !destinationIsGone)
            {
                return new SyncAction<TItem>
                {
                    Kind = Delete,
                    Direction = SourceToDestination,
                    Item = currentDestinationItem,
                    DeleteId = link.DestinationId,
                    Reason = "Source item absent (deleted or out of filter scope)",
                };
            }

            string sourceSkipReason = jobOptions.DeletePolicy == Ignore
                ? "Source item absent but delete policy is Ignore"
                : "Source item absent but sync direction does not propagate source-side deletions";

            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                Item = currentDestinationItem,
                Reason = sourceSkipReason,
            };
        }

        // --- Destination gone ---
        if (destinationIsGone)
        {
            if (force && jobOptions.SyncMode != Reverse)
            {
                // --force: act as if DB is empty. Source is present but destination is gone.
                // On first run with empty DB we would Create on destination — do the same here
                // instead of trying to Update a missing item (→ 404).
                return new SyncAction<TItem>
                {
                    Kind = Create,
                    Direction = SourceToDestination,
                    Item = currentSourceItem,
                    Reason = "Destination absent; recreating per --force",
                };
            }

            if (jobOptions.SyncMode != Forward
                && jobOptions.DeletePolicy != Ignore)
            {
                return new SyncAction<TItem>
                {
                    Kind = Delete,
                    Direction = DestinationToSource,
                    Item = currentSourceItem,
                    DeleteId = link.SourceId,
                    Reason = "Destination item absent (deleted or out of filter scope)",
                };
            }

            string destinationSkipReason = jobOptions.SyncMode == Forward
                ? "Destination item absent but sync direction does not propagate destination-side deletions"
                : "Destination item absent but delete policy is Ignore";

            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                Item = currentSourceItem,
                Reason = destinationSkipReason,
            };
        }

        // Both items are present and non-deleted from this point on.
        bool sourceChanged = force || HasChanged(currentSourceItem!, link, DestinationToSource);
        bool destinationChanged = force || HasChanged(currentDestinationItem!, link, SourceToDestination);

        // If the payload is identical on both sides there is nothing to write — return a Skip so the
        // caller can still record/update the link without re-uploading the item.
        if (!jobOptions.Force && (sourceChanged || destinationChanged)
            && ContentHashHelper.HaveIdenticalContent(currentSourceItem!, currentDestinationItem!))
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = skipDirection,
                Item = currentSourceItem,
                MatchedTargetItem = currentDestinationItem,
                Reason = "Content identical on both sides",
            };
        }

        return jobOptions.SyncMode switch
        {
            Forward when !sourceChanged => null,
            Forward when destinationChanged => ResolveConflict(currentSourceItem, currentDestinationItem, SourceToDestination, jobOptions),
            Forward => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = currentSourceItem,
                MatchedTargetItem = currentDestinationItem,
                Reason = "Item changed on source side",
            },

            Reverse when !destinationChanged => null,
            Reverse when sourceChanged => ResolveConflict(currentSourceItem, currentDestinationItem, DestinationToSource, jobOptions),
            Reverse => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = currentDestinationItem,
                MatchedTargetItem = currentSourceItem,
                Reason = "Item changed on destination side",
            },

            // Bidirectional
            _ when !sourceChanged && !destinationChanged => null,
            _ when sourceChanged && !destinationChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = currentSourceItem,
                MatchedTargetItem = currentDestinationItem,
                Reason = "Item changed on source side",
            },
            _ when !sourceChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = currentDestinationItem,
                MatchedTargetItem = currentSourceItem,
                Reason = "Item changed on destination side",
            },
            // Both changed: resolve using conflict policy. Prefer the A-originating direction
            // as the primary so that SourceWins and LastWriteWins work naturally; DestinationWins is
            // handled by checking the policy explicitly.
            _ when jobOptions.ConflictPolicy == DestinationWins && currentDestinationItem != null => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = currentDestinationItem,
                MatchedTargetItem = currentSourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            _ => ResolveConflict(currentSourceItem!, currentDestinationItem, SourceToDestination, jobOptions),
        };
    }

    /// <summary>
    /// Plans actions for unlinked source items against the eligible pool of target items.
    /// </summary>
    /// <param name="jobOptions">Job configuration.</param>
    /// <param name="sourceItems">All items on the source side.</param>
    /// <param name="targetItems">All items on the target side.</param>
    /// <param name="direction">Which side is the target.</param>
    /// <param name="linkedSourceIds">Source-side ids that are already part of an existing link.</param>
    /// <param name="linkedTargetIds">Target-side ids that are already part of an existing link.</param>
    /// <param name="reservedSourceIds">
    /// Source ids already claimed by a planned action; updated in place as new actions are added.
    /// </param>
    /// <param name="reservedTargetIds">
    /// Target ids already claimed by a planned action; updated in place as new actions are added.
    /// </param>
    /// <param name="actions">Accumulator list for planned actions.</param>
    private void PlanUnlinkedItems<TItem>(
        JobOptions jobOptions,
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> targetItems,
        SyncDirection direction,
        IReadOnlySet<string> linkedSourceIds,
        IReadOnlySet<string> linkedTargetIds,
        HashSet<string> reservedSourceIds,
        HashSet<string> reservedTargetIds,
        List<SyncAction<TItem>> actions) where TItem : CanonicalItem
    {
        // Eligible target pool: unlinked, unreserved, not deleted.
        var eligibleTargets = targetItems
            .Where(t => !t.IsDeleted && !linkedTargetIds.Contains(t.Provenance.ProviderId) && !reservedTargetIds.Contains(t.Provenance.ProviderId))
            .ToList();

        var unlinkedSources = sourceItems
            .Where(s => !s.IsDeleted && !linkedSourceIds.Contains(s.Provenance.ProviderId) && !reservedSourceIds.Contains(s.Provenance.ProviderId))
            .ToList();

        // Build a candidate map using the two-step best-match strategy.
        var candidatesBySourceId = ItemMatcher.BuildDuplicateCandidateMap(unlinkedSources, eligibleTargets);

        // For each eligible target that appears as the unique candidate for exactly one source,
        // count how many sources point to it. If > 1 the match is ambiguous (many-to-one).
        var competitionForTarget = candidatesBySourceId
            .Where(kv => kv.Value.Length == 1)
            .GroupBy(kv => kv.Value[0].Provenance.ProviderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var item in unlinkedSources)
        {
            // Re-check: a previous iteration in this pass may have reserved this source.
            if (reservedSourceIds.Contains(item.Provenance.ProviderId))
            {
                continue;
            }

            var candidates = candidatesBySourceId[item.Provenance.ProviderId];

            if (candidates.Length == 0)
            {
                actions.Add(new SyncAction<TItem>
                {
                    Kind = Create,
                    Direction = direction,
                    Item = item,
                    Reason = "New item on source side",
                });
                reservedSourceIds.Add(item.Provenance.ProviderId);
                continue;
            }

            if (candidates.Length > 1)
            {
                logger.LogWarning(
                    "Item {SourceId} has {MatchCount} matching contacts on side {TargetSide}; skipping auto-linking",
                    item.Provenance.ProviderId,
                    candidates.Length,
                    direction);

                actions.Add(new SyncAction<TItem>
                {
                    Kind = SyncActionKind.Skip,
                    Direction = direction,
                    Item = item,
                    Reason = "Multiple matching contacts on target side",
                });
                continue;
            }

            // Exactly one candidate.
            var target = candidates[0];

            if (reservedTargetIds.Contains(target.Provenance.ProviderId))
            {
                // The target was claimed by a previous action after the eligible pool was built.
                // The linked endpoint is unavailable as a candidate, so treat as zero matches → create.
                actions.Add(new SyncAction<TItem>
                {
                    Kind = Create,
                    Direction = direction,
                    Item = item,
                    Reason = "New item on source side",
                });
                reservedSourceIds.Add(item.Provenance.ProviderId);
                continue;
            }

            if (competitionForTarget.GetValueOrDefault(target.Provenance.ProviderId, 0) > 1)
            {
                // Multiple source items compete for this target → ambiguous, skip.
                logger.LogWarning(
                    "Item {SourceId} matches target contact {TargetId}, but that target also matches other source contacts on side {TargetSide}; skipping auto-linking",
                    item.Provenance.ProviderId,
                    target.Provenance.ProviderId,
                    direction);

                actions.Add(new SyncAction<TItem>
                {
                    Kind = SyncActionKind.Skip,
                    Direction = direction,
                    Item = item,
                    Reason = "Target contact matches multiple source contacts",
                });
                continue;
            }

            // Clean 1:1 match — skip if content is already identical.
            if (!jobOptions.Force && ContentHashHelper.HaveIdenticalContent(item, target))
            {
                actions.Add(new SyncAction<TItem>
                {
                    Kind = SyncActionKind.Skip,
                    Direction = direction,
                    Item = item,
                    MatchedTargetItem = target,
                    Reason = "Content identical on both sides",
                });
            }
            else
            {
                actions.Add(new SyncAction<TItem>
                {
                    Kind = Update,
                    Direction = direction,
                    Item = item,
                    MatchedTargetItem = target,
                    Reason = "Matched existing contact on target side",
                });
            }
            reservedSourceIds.Add(item.Provenance.ProviderId);
            reservedTargetIds.Add(target.Provenance.ProviderId);
        }
    }

    private static bool HasChanged(CanonicalItem item, LinkStateRow link, SyncDirection direction)
    {
        if (direction == DestinationToSource)
        {
            if (item.Provenance.Version != null && link.SourceVersion != null)
            {
                return item.Provenance.Version != link.SourceVersion;
            }

            if (item.Provenance.ContentHash != null && link.SourceHash != null)
            {
                return item.Provenance.ContentHash != link.SourceHash;
            }
        }
        else
        {
            if (item.Provenance.Version != null && link.DestinationVersion != null)
            {
                return item.Provenance.Version != link.DestinationVersion;
            }

            if (item.Provenance.ContentHash != null && link.DestinationHash != null)
            {
                return item.Provenance.ContentHash != link.DestinationHash;
            }
        }

        // Without version/hash info, assume changed.
        return true;
    }

    private static SyncAction<TItem> ResolveConflict<TItem>(
        TItem? sourceItem,
        TItem? destinationItem,
        SyncDirection direction,
        JobOptions jobOptions) where TItem : CanonicalItem =>
        jobOptions.ConflictPolicy switch
        {
            SourceWins when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = sourceItem,
                MatchedTargetItem = destinationItem,
                Reason = "Conflict: source wins per policy",
            },
            SourceWins => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                Item = destinationItem,
                Reason = "Conflict: source wins per policy",
            },
            DestinationWins when direction == DestinationToSource => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = destinationItem,
                MatchedTargetItem = sourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            DestinationWins => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                Item = sourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            ConflictPolicy.Skip => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = direction,
                Item = direction == SourceToDestination ? sourceItem : destinationItem,
                Reason = "Conflict: skipped per policy",
            },
            _ => ResolveLastWriteWinsConflict(sourceItem, destinationItem, direction),
        };

    private static SyncAction<TItem> ResolveLastWriteWinsConflict<TItem>(
        TItem? sourceItem,
        TItem? destinationItem,
        SyncDirection direction) where TItem : CanonicalItem
    {
        var sourceLastModified = sourceItem?.Provenance.LastModified;
        var destinationLastModified = destinationItem?.Provenance.LastModified;

        bool sourceWins = sourceLastModified > destinationLastModified
                          || (sourceLastModified == destinationLastModified && direction == SourceToDestination)
                          || (sourceLastModified is not null && destinationLastModified is null);

        return sourceWins switch
        {
            true when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = sourceItem,
                MatchedTargetItem = destinationItem,
                Reason = "Conflict: last write wins per policy",
            },
            true => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                Item = destinationItem,
                Reason = "Conflict: source wins per last-write-wins policy",
            },
            false when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                Item = sourceItem,
                Reason = "Conflict: destination wins but sync mode does not permit reverse write",
            },
            false => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = destinationItem,
                MatchedTargetItem = sourceItem,
                Reason = "Conflict: last write wins per policy",
            }
        };
    }
}
