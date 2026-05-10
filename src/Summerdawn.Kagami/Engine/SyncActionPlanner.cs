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
/// <see cref="JobOptions.Force"/> bypasses per-item change detection for both persisted and
/// inferred links, causing every in-scope item to be written unconditionally regardless of
/// whether the content appears identical on both sides. Use <c>--force</c> only when normal
/// change detection via version or content hash is known to be unreliable (for example, when
/// destination data has drifted in ways Kagami cannot detect). For routine syncs, the default
/// change-detection path is sufficient.
/// </para>
/// </remarks>
public sealed class SyncActionPlanner(LinkCreator linkCreator)
{
    /// <summary>
    /// Computes the full set of sync actions from current snapshots of both sides and existing links.
    /// </summary>
    /// <param name="jobOptions">Options governing the current sync run.</param>
    /// <param name="sourceItems">
    /// Raw (unfiltered) items observed on the source side in this run.  When a cursor is
    /// available these are the delta items only; when performing a full scan they are all items.
    /// </param>
    /// <param name="destinationItems">Raw (unfiltered) items observed on the destination side.</param>
    /// <param name="existingLinks">Persisted link-state rows for the current job partition.</param>
    /// <param name="filter">
    /// Optional filter that scopes which items are relevant to this job.  Passed to
    /// <see cref="LinkExaminer"/> so it can distinguish items that moved out of scope from items
    /// that are genuinely unchanged.
    /// </param>
    public IReadOnlyList<SyncAction<TItem>> PlanActions<TItem>(
        JobOptions jobOptions,
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> destinationItems,
        IReadOnlyList<LinkStateRow> existingLinks,
        IFilter<TItem>? filter = null) where TItem : CanonicalItem
    {
        var links = linkCreator.BuildLinks(sourceItems, destinationItems, existingLinks);
        var actions = new List<SyncAction<TItem>>(links.Count);

        foreach (var link in links)
        {
            var examined = LinkExaminer.Examine(link, jobOptions, filter);
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
        where TItem : CanonicalItem
    {
        // Items that are observable in this run but fall entirely outside the current filter scope
        // and have no prior persisted record are silently ignored — no action, no skip — to avoid
        // noisy logs for contacts that were never part of the synced set.
        if (!examined.IsRelevantToCurrentScope)
        {
            return null;
        }

        return examined.Kind switch
        {
            LinkKind.Persisted => PlanPersistedLink(examined, jobOptions),
            LinkKind.Inferred => PlanInferredLink(examined, jobOptions),
            LinkKind.Unmatched => PlanUnmatchedLink(examined, jobOptions),
            LinkKind.Ambiguous => PlanAmbiguousLink(examined, jobOptions),
            _ => null,
        };
    }

    // -----------------------------------------------------------------------
    // Persisted link planning
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans an action for a link that is backed by a persisted <see cref="Summerdawn.Kagami.Persistence.LinkStateRow"/>.
    /// </summary>
    private static SyncAction<TItem>? PlanPersistedLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var link = examined;
        var row = link.PersistedState!;
        bool force = jobOptions.Force;

        // MovedOutOfScope is treated the same as Deleted for delete propagation: the item no
        // longer belongs to the mirrored sync set, so we should remove the mirrored copy.
        // Crucially, the persisted link state must NOT encode this as a source deletion.
        bool sourceIsGone = examined.SourceActivity is Deleted or MovedOutOfScope;

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
                        ExaminedLink = examined,
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
                        ExaminedLink = examined,
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
                    ExaminedLink = examined,
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
                            ExaminedLink = examined,
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
                        ExaminedLink = examined,
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
                    ExaminedLink = examined,
                    Reason = destinationSkipReason,
                };
            }
        }

        // Both items are present (or force is active and both present).
        bool sourceChanged = force || examined.SourceActivity == Modified;
        bool destinationChanged = force || examined.DestinationActivity == Modified;

        if (!force && (sourceChanged || destinationChanged)
            && link.SourceItem is not null && link.DestinationItem is not null
            && ContentHashHelper.HaveIdenticalContent(link.SourceItem, link.DestinationItem))
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = skipDirection,
                ExaminedLink = examined,
                Reason = "Content identical on both sides",
            };
        }

        return jobOptions.SyncMode switch
        {
            Forward when !sourceChanged => null,
            Forward when destinationChanged => ResolveConflict(examined, SourceToDestination, jobOptions),
            Forward => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                ExaminedLink = examined,
                Reason = "Item changed on source side",
            },

            Reverse when !destinationChanged => null,
            Reverse when sourceChanged => ResolveConflict(examined, DestinationToSource, jobOptions),
            Reverse => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Item changed on destination side",
            },

            // Bidirectional
            _ when !sourceChanged && !destinationChanged => null,
            _ when sourceChanged && !destinationChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                ExaminedLink = examined,
                Reason = "Item changed on source side",
            },
            _ when !sourceChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Item changed on destination side",
            },
            _ when jobOptions.ConflictPolicy == DestinationWins && link.DestinationItem != null => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Conflict: destination wins per policy",
            },
            _ => ResolveConflict(examined, SourceToDestination, jobOptions),
        };
    }

    // -----------------------------------------------------------------------
    // Inferred link planning (new match, no prior persisted state)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans an action for a link inferred by similarity matching; no persisted state exists.
    /// </summary>
    /// <remarks>
    /// When <see cref="JobOptions.Force"/> is set, the content-sameness check is bypassed and
    /// an unconditional write is issued — consistent with how persisted links are handled under
    /// <c>--force</c>. Without <c>--force</c>, identical content on both sides produces a
    /// <see cref="SyncActionKind.Skip"/> so that already-in-sync pairs are registered without a
    /// redundant round-trip write.
    /// </remarks>
    private static SyncAction<TItem>? PlanInferredLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var link = examined;

        // An inferred link may only be produced when neither side was previously linked.
        // Both sides are present (Created), so SourceItem and DestinationItem are non-null.
        var source = link.SourceItem!;
        var dest = link.DestinationItem!;

        // Without --force: skip the write when both sides already carry identical content.
        // Under --force: bypass this guard so the pair is written unconditionally.
        if (!jobOptions.Force && ContentHashHelper.HaveIdenticalContent(source, dest))
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = skipDirection,
                ExaminedLink = examined,
                Reason = "Content identical on both sides",
            };
        }

        return jobOptions.SyncMode == Reverse
            ? new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Matched existing contact on target side",
            }
            : new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                ExaminedLink = examined,
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
        var link = examined;

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
                ExaminedLink = examined,
                Reason = "New item on source side",
            };
        }

        if (link.DestinationItem != null)
        {
            // Destination-only: no source match found.
            // In Forward mode with Mirror policy on a full scan, the source is authoritative
            // so this item should be deleted from the destination.
            if (jobOptions.SyncMode == Forward)
            {
                if (jobOptions.DeletePolicy == Mirror && jobOptions.Full)
                {
                    return new SyncAction<TItem>
                    {
                        Kind = Delete,
                        Direction = SourceToDestination,
                        ExaminedLink = examined,
                        Reason = "Destination-only item; source is authoritative (--prune)",
                    };
                }

                return null;
            }

            return new SyncAction<TItem>
            {
                Kind = Create,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "New item on destination side",
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
        var link = examined;

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
                ExaminedLink = examined,
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
                ExaminedLink = examined,
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
        ExaminedLink<TItem> examined,
        SyncDirection direction,
        JobOptions jobOptions) where TItem : CanonicalItem =>
        jobOptions.ConflictPolicy switch
        {
            SourceWins when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                ExaminedLink = examined,
                Reason = "Conflict: source wins per policy",
            },
            SourceWins => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Conflict: source wins per policy",
            },
            DestinationWins when direction == DestinationToSource => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Conflict: destination wins per policy",
            },
            DestinationWins => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                ExaminedLink = examined,
                Reason = "Conflict: destination wins per policy",
            },
            ConflictPolicy.Skip => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = direction,
                ExaminedLink = examined,
                Reason = "Conflict: skipped per policy",
            },
            _ => ResolveLastWriteWinsConflict(examined, direction),
        };

    /// <summary>
    /// Resolves a conflict using last-write-wins: the item with the more recent
    /// <see cref="ItemProvenance.LastModified"/> timestamp wins.
    /// </summary>
    private static SyncAction<TItem> ResolveLastWriteWinsConflict<TItem>(
        ExaminedLink<TItem> examined,
        SyncDirection direction) where TItem : CanonicalItem
    {
        var sourceItem = examined.SourceItem;
        var destinationItem = examined.DestinationItem;
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
                ExaminedLink = examined,
                Reason = "Conflict: last write wins per policy",
            },
            true => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Conflict: source wins per last-write-wins policy",
            },
            false when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                ExaminedLink = examined,
                Reason = "Conflict: destination wins but sync mode does not permit reverse write",
            },
            false => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                ExaminedLink = examined,
                Reason = "Conflict: last write wins per policy",
            }
        };
    }
}
