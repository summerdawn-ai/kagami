using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Configuration.ConflictPolicy;
using static Summerdawn.Kagami.Configuration.DeletePolicy;
using static Summerdawn.Kagami.Configuration.SyncMode;
using static Summerdawn.Kagami.Engine.SideActivity;
using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Plans sync actions from current remote state and persisted link state using a link-centric pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Planning uses three components in sequence:
/// </para>
/// <list type="number">
///   <item>
///     <term><see cref="LinkCreator"/></term>
///     <description>Assembles in-memory <see cref="Link{TItem}"/> instances from source items,
///     destination items, and relevant persisted link rows.</description>
///   </item>
///   <item>
///     <term><see cref="LinkExaminer"/></term>
///     <description>Examines each link and reports the observed activity on each side,
///     independent of job policy.</description>
///   </item>
///   <item>
///     <term>Action mapping</term>
///     <description>Translates observed activity together with <see cref="JobOptions"/> into
///     proposed <see cref="SyncAction{TItem}"/> instances.</description>
///   </item>
/// </list>
/// <para>
/// <see cref="JobOptions.Force"/> bypasses per-item change detection for persisted links,
/// causing every in-scope item to be written unconditionally.
/// For newly inferred pairs the content-sameness check is still applied even in Force mode:
/// when both sides already carry identical content the link is registered without re-writing.
/// </para>
/// </remarks>
public sealed class SyncActionPlanner(LinkCreator linkCreator)
{
    /// <summary>
    /// Computes the full set of sync actions from current snapshots of both sides and existing links.
    /// </summary>
    public IReadOnlyList<SyncAction<TItem>> PlanActions<TItem>(
        JobOptions jobOptions,
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> destinationItems,
        IReadOnlyList<LinkStateRow> existingLinks) where TItem : CanonicalItem
    {
        var links = linkCreator.BuildLinks(sourceItems, destinationItems, existingLinks);
        var actions = new List<SyncAction<TItem>>(links.Count);

        foreach (var link in links)
        {
            var examined = LinkExaminer.Examine(link);
            var action = MapToAction(examined, jobOptions);
            if (action != null)
            {
                actions.Add(action);
            }
        }

        return actions;
    }

    // -----------------------------------------------------------------------
    // Top-level dispatch
    // -----------------------------------------------------------------------

    private static SyncAction<TItem>? MapToAction<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem =>
        examined.Link.Kind switch
        {
            LinkKind.Persisted => PlanPersistedLink(examined, jobOptions),
            LinkKind.Inferred => PlanInferredLink(examined, jobOptions),
            LinkKind.Unmatched => PlanUnmatchedLink(examined, jobOptions),
            LinkKind.Ambiguous => PlanAmbiguousLink(examined, jobOptions),
            _ => null,
        };

    // -----------------------------------------------------------------------
    // Persisted link planning
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans an action for a link that is backed by a persisted <see cref="Summerdawn.Kagami.Persistence.LinkStateRow"/>.
    /// </summary>
    private static SyncAction<TItem>? PlanPersistedLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var link = examined.Link;
        var row = link.PersistedState!;
        bool force = jobOptions.Force;

        bool sourceIsGone = examined.SourceActivity == Deleted;

        // DestinationGone covers both "tracked and now absent" (Deleted) and
        // "was never created" (Absent, i.e. DestinationId was null).
        bool destinationIsGone = examined.DestinationActivity is Deleted or Absent;

        // --- Force mode: act as if DB is empty ---
        if (force)
        {
            if (sourceIsGone)
            {
                // Source is absent — nothing to push regardless of destination state.
                return null;
            }

            if (destinationIsGone)
            {
                if (jobOptions.SyncMode != Reverse)
                {
                    return new SyncAction<TItem>
                    {
                        Kind = Create,
                        Direction = SourceToDestination,
                        Item = link.SourceItem,
                        Reason = "Destination absent; recreating per --force",
                    };
                }

                // Reverse mode + destination gone: no action (can't write source in this mode).
                return null;
            }

            // Both present — fall through to update logic with forced-changed semantics.
        }
        else
        {
            // --- Source gone ---
            if (sourceIsGone)
            {
                if (jobOptions.SyncMode != Reverse
                    && jobOptions.DeletePolicy != Ignore
                    && row.DestinationId != null
                    && examined.DestinationActivity != Deleted)
                {
                    return new SyncAction<TItem>
                    {
                        Kind = Delete,
                        Direction = SourceToDestination,
                        Item = link.DestinationItem,
                        DeleteId = row.DestinationId,
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
                    Item = link.DestinationItem,
                    Reason = sourceSkipReason,
                };
            }

            // --- Destination gone or absent ---
            if (destinationIsGone)
            {
                if (examined.DestinationActivity == Absent)
                {
                    // Destination was never created; create it now if mode allows.
                    if (jobOptions.SyncMode != Reverse)
                    {
                        return new SyncAction<TItem>
                        {
                            Kind = Create,
                            Direction = SourceToDestination,
                            Item = link.SourceItem,
                            Reason = "Destination not yet created",
                        };
                    }

                    return null;
                }

                // Destination is Deleted.
                if (jobOptions.SyncMode != Forward && jobOptions.DeletePolicy != Ignore)
                {
                    return new SyncAction<TItem>
                    {
                        Kind = Delete,
                        Direction = DestinationToSource,
                        Item = link.SourceItem,
                        DeleteId = row.SourceId,
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
                    Item = link.SourceItem,
                    Reason = destinationSkipReason,
                };
            }
        }

        // Both items are present (or force is active and both present).
        bool sourceChanged = force || examined.SourceActivity == Modified;
        bool destinationChanged = force || examined.DestinationActivity == Modified;

        if (!force && (sourceChanged || destinationChanged)
            && ContentHashHelper.HaveIdenticalContent(link.SourceItem!, link.DestinationItem!))
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = skipDirection,
                Item = link.SourceItem,
                MatchedTargetItem = link.DestinationItem,
                Reason = "Content identical on both sides",
            };
        }

        return jobOptions.SyncMode switch
        {
            Forward when !sourceChanged => null,
            Forward when destinationChanged => ResolveConflict(link.SourceItem, link.DestinationItem, SourceToDestination, jobOptions),
            Forward => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = link.SourceItem,
                MatchedTargetItem = link.DestinationItem,
                Reason = "Item changed on source side",
            },

            Reverse when !destinationChanged => null,
            Reverse when sourceChanged => ResolveConflict(link.SourceItem, link.DestinationItem, DestinationToSource, jobOptions),
            Reverse => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = link.DestinationItem,
                MatchedTargetItem = link.SourceItem,
                Reason = "Item changed on destination side",
            },

            // Bidirectional
            _ when !sourceChanged && !destinationChanged => null,
            _ when sourceChanged && !destinationChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = link.SourceItem,
                MatchedTargetItem = link.DestinationItem,
                Reason = "Item changed on source side",
            },
            _ when !sourceChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = link.DestinationItem,
                MatchedTargetItem = link.SourceItem,
                Reason = "Item changed on destination side",
            },
            _ when jobOptions.ConflictPolicy == DestinationWins && link.DestinationItem != null => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = link.DestinationItem,
                MatchedTargetItem = link.SourceItem,
                Reason = "Conflict: destination wins per policy",
            },
            _ => ResolveConflict(link.SourceItem!, link.DestinationItem, SourceToDestination, jobOptions),
        };
    }

    // -----------------------------------------------------------------------
    // Inferred link planning (new match, no prior persisted state)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans an action for a link inferred by similarity matching; no persisted state exists.
    /// </summary>
    /// <remarks>
    /// The content-sameness check is always applied for inferred links — even when
    /// <see cref="JobOptions.Force"/> is set — so that first-time pairs with identical content are
    /// simply registered without an unnecessary round-trip write.
    /// </remarks>
    private static SyncAction<TItem>? PlanInferredLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var link = examined.Link;

        // An inferred link may only be produced when neither side was previously linked.
        // Both sides are present (Created), so SourceItem and DestinationItem are non-null.
        var source = link.SourceItem!;
        var dest = link.DestinationItem!;

        // Always check content equality for new pairs — even under --force — to avoid
        // re-writing data that is already in sync.
        if (ContentHashHelper.HaveIdenticalContent(source, dest))
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = skipDirection,
                Item = source,
                MatchedTargetItem = dest,
                Reason = "Content identical on both sides",
            };
        }

        return jobOptions.SyncMode == Reverse
            ? new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Item = dest,
                MatchedTargetItem = source,
                Reason = "Matched existing contact on target side",
            }
            : new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Item = source,
                MatchedTargetItem = dest,
                Reason = "Matched existing contact on target side",
            };
    }

    // -----------------------------------------------------------------------
    // Unmatched link planning (one side only, no match found)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans an action for a one-sided link where no matching item was found on the other side.
    /// </summary>
    private static SyncAction<TItem>? PlanUnmatchedLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var link = examined.Link;

        if (link.SourceItem != null)
        {
            // Source-only: create on destination (unless restricted by SyncMode).
            if (jobOptions.SyncMode == Reverse)
            {
                return null;
            }

            return new SyncAction<TItem>
            {
                Kind = Create,
                Direction = SourceToDestination,
                Item = link.SourceItem,
                Reason = "New item on source side",
            };
        }

        if (link.DestinationItem != null)
        {
            // Destination-only: create on source (unless restricted by SyncMode).
            if (jobOptions.SyncMode == Forward)
            {
                return null;
            }

            return new SyncAction<TItem>
            {
                Kind = Create,
                Direction = DestinationToSource,
                Item = link.DestinationItem,
                Reason = "New item on source side",
            };
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Ambiguous link planning (matching is unsafe)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans a skip action for an ambiguous link, or returns <c>null</c> when the sync mode
    /// does not process the active side.
    /// </summary>
    private static SyncAction<TItem>? PlanAmbiguousLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var link = examined.Link;

        if (link.SourceItem != null)
        {
            // Source-side ambiguity: relevant only when mode processes source→destination.
            if (jobOptions.SyncMode == Reverse)
            {
                return null;
            }

            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                Item = link.SourceItem,
                Reason = "Multiple matching contacts on target side",
            };
        }

        if (link.DestinationItem != null)
        {
            // Destination-side ambiguity (contested target): relevant only when mode processes destination→source.
            if (jobOptions.SyncMode == Forward)
            {
                return null;
            }

            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                Item = link.DestinationItem,
                Reason = "Target contact matches multiple source contacts",
            };
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Conflict resolution helpers (identical to previous Planner logic)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a conflict using the configured <see cref="JobOptions.ConflictPolicy"/>.
    /// </summary>
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

    /// <summary>
    /// Resolves a conflict using last-write-wins: the item with the more recent
    /// <see cref="ItemProvenance.LastModified"/> timestamp wins.
    /// </summary>
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
