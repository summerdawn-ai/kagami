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
/// inferred links, treating matched items as changed even when their content appears identical.
/// The configured sync direction and conflict policy still choose which side is written, if any.
/// Use <c>--force</c> only when normal change detection via version or content hash is known to
/// be unreliable (for example, when destination data has drifted in ways Kagami cannot detect).
/// For routine syncs, the default change-detection path is sufficient.
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

    /// <summary>
    /// Maps one examined link to its single policy-driven synchronization action.
    /// </summary>
    /// <remarks>
    /// Filter relevance is evaluated before link kind so never-synced items outside the current
    /// scope do not generate noisy skip actions. Each link kind then owns its distinct lifecycle:
    /// persisted links use baselines, inferred links establish a safe match, and unmatched or
    /// ambiguous links avoid unsafe writes.
    /// </remarks>
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
    /// <remarks>
    /// Persisted links retain provider identity and synchronization baselines. Force mode ignores
    /// their change baselines, but still uses their IDs to update or delete the established pair
    /// safely. Normal mode first handles terminal deletion state, then deletion propagation, and
    /// finally update/conflict planning when both items remain in scope.
    /// </remarks>
    private static SyncAction<TItem>? PlanPersistedLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        var row = examined.PersistedState!;
        bool force = jobOptions.Force;

        // MovedOutOfScope is treated the same as Deleted for delete propagation: the item no
        // longer belongs to the mirrored sync set, so we should remove the mirrored copy.
        // Crucially, the persisted link state must NOT encode this as a source deletion.
        bool sourceIsGone = examined.SourceActivity is Deleted or MovedOutOfScope;

        // DestinationGone covers both "tracked and now absent" (Deleted) and
        // "was never created" (Absent, i.e. DestinationId was null).
        bool destinationIsGone = examined.DestinationActivity is Deleted or Absent;

        // --- Force mode: ignore comparison baselines, retaining linked provider identities ---
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
                        Link = examined,
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
            // A provider may omit a previously consumed tombstone on a later delta. Combine the
            // current gone state with persisted terminal state before planning propagation.
            if ((sourceIsGone || row.SourceDeleted)
                && (destinationIsGone || row.DestinationDeleted))
            {
                return null;
            }

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
                        Link = examined,
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
                    Link = examined,
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
                            Link = examined,
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
                        Link = examined,
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
                    Link = examined,
                    Reason = destinationSkipReason,
                };
            }
        }

        // Both items are present (or force is active and both present). Keep the content
        // comparison even under --force so diagnostics can distinguish a real difference from
        // an otherwise redundant write that force explicitly requested.
        bool contentIdentical = examined.SourceItem is not null
            && examined.DestinationItem is not null
            && ContentHashHelper.HaveIdenticalContent(examined.SourceItem, examined.DestinationItem);
        bool sourceChanged = force || examined.SourceActivity == Modified;
        bool destinationChanged = force || examined.DestinationActivity == Modified;

        if (!force && (sourceChanged || destinationChanged) && contentIdentical)
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = None,
                Direction = skipDirection,
                Link = examined,
                Reason = "Content identical on both sides",
            };
        }

        var action = jobOptions.SyncMode switch
        {
            Forward when !sourceChanged => null,
            Forward when destinationChanged => ResolveConflict(examined, SourceToDestination, jobOptions),
            Forward => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Link = examined,
                ReasonKind = GetUpdateReason(jobOptions, contentIdentical),
            },

            Reverse when !destinationChanged => null,
            Reverse when sourceChanged => ResolveConflict(examined, DestinationToSource, jobOptions),
            Reverse => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Link = examined,
                ReasonKind = GetUpdateReason(jobOptions, contentIdentical),
            },

            // Bidirectional
            _ when !sourceChanged && !destinationChanged => null,
            _ when sourceChanged && !destinationChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Link = examined,
                ReasonKind = GetUpdateReason(jobOptions, contentIdentical),
            },
            _ when !sourceChanged => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Link = examined,
                ReasonKind = GetUpdateReason(jobOptions, contentIdentical),
            },
            _ => ResolveBidirectionalConflict(examined, jobOptions),
        };

        return ApplyForceReason(
            action,
            force,
            contentIdentical,
            examined.SourceItem?.Provenance.LastModified == examined.DestinationItem?.Provenance.LastModified,
            examined.SourceActivity == Modified,
            examined.DestinationActivity == Modified);
    }

    // -----------------------------------------------------------------------
    // Inferred link planning (new match, no prior persisted state)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Plans an action for a link inferred by similarity matching; no persisted state exists.
    /// </summary>
    /// <remarks>
    /// When <see cref="JobOptions.Force"/> is set, the content-sameness check is bypassed and
    /// the configured conflict policy chooses the write direction, consistent with persisted
    /// links. Without <c>--force</c>, identical content on both sides produces a
    /// <see cref="SyncActionKind.Skip"/> so that already-in-sync pairs are registered without a
    /// redundant round-trip write.
    /// </remarks>
    private static SyncAction<TItem>? PlanInferredLink<TItem>(ExaminedLink<TItem> examined, JobOptions jobOptions)
        where TItem : CanonicalItem
    {
        // An inferred link may only be produced when neither side was previously linked.
        // Both sides are present (Created), so SourceItem and DestinationItem are non-null.
        var source = examined.SourceItem!;
        var dest = examined.DestinationItem!;

        // Without --force: skip the write when both sides already carry identical content.
        // Under --force: bypass this guard and resolve the matched pair as a conflict.
        if (!jobOptions.Force && ContentHashHelper.HaveIdenticalContent(source, dest))
        {
            var skipDirection = jobOptions.SyncMode == Reverse ? DestinationToSource : SourceToDestination;
            return new SyncAction<TItem>
            {
                Kind = None,
                Direction = skipDirection,
                Link = examined,
                Reason = "Content identical on both sides",
            };
        }

        if (jobOptions.Force)
        {
            // With no stored baseline, force treats a matched pair as changed on both sides.
            // Apply the same conflict policy as persisted pairs rather than privileging the
            // source merely because the link was inferred during this run.
            var action = jobOptions.SyncMode switch
            {
                Reverse => ResolveConflict(examined, DestinationToSource, jobOptions),
                Bidirectional => ResolveBidirectionalConflict(examined, jobOptions),
                _ => ResolveConflict(examined, SourceToDestination, jobOptions),
            };

            return ApplyForceReason(
                action,
                force: true,
                contentIdentical: ContentHashHelper.HaveIdenticalContent(source, dest),
                modificationTimestampsEqual: source.Provenance.LastModified == dest.Provenance.LastModified,
                sourceModified: false,
                destinationModified: false);
        }

        return jobOptions.SyncMode == Reverse
            ? new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Link = examined,
                Reason = "Matched existing item on target side",
            }
            : new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Link = examined,
                Reason = "Matched existing item on target side",
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
        if (examined.SourceItem != null)
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
                Link = examined,
                Reason = "New item on source side",
            };
        }

        if (examined.DestinationItem != null)
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
                        Link = examined,
                        Reason = "Destination-only item; source is authoritative (--prune)",
                    };
                }

                return null;
            }

            return new SyncAction<TItem>
            {
                Kind = Create,
                Direction = DestinationToSource,
                Link = examined,
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
        if (examined.SourceItem != null)
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
                Link = examined,
                Reason = "Multiple matching items on target side",
            };
        }

        if (examined.DestinationItem != null)
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
                Link = examined,
                Reason = "Target item matches multiple source items",
            };
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Conflict resolution helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a bidirectional conflict by selecting the winning write direction first.
    /// </summary>
    /// <remarks>
    /// The selected direction lets <see cref="ResolveConflict{TItem}"/> produce an update for
    /// either winner. This is necessary for last-write-wins: passing a fixed source-to-destination
    /// direction would turn a destination-winning conflict into a directional skip.
    /// </remarks>
    private static SyncAction<TItem> ResolveBidirectionalConflict<TItem>(
        ExaminedLink<TItem> examined,
        JobOptions jobOptions) where TItem : CanonicalItem
    {
        var direction = jobOptions.ConflictPolicy switch
        {
            DestinationWins => DestinationToSource,
            LastWriteWins when !SourceWinsLastWriteWins(examined, SourceToDestination) => DestinationToSource,
            _ => SourceToDestination,
        };

        return ResolveConflict(examined, direction, jobOptions);
    }

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
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictWinner,
                WinningDirection = SourceToDestination,
                ConflictPolicyName = "source-wins",
            },
            SourceWins => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictWinnerCannotUpdate,
                WinningDirection = SourceToDestination,
                ConflictPolicyName = "source-wins",
            },
            DestinationWins when direction == DestinationToSource => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictWinner,
                WinningDirection = DestinationToSource,
                ConflictPolicyName = "destination-wins",
            },
            DestinationWins => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictWinnerCannotUpdate,
                WinningDirection = DestinationToSource,
                ConflictPolicyName = "destination-wins",
            },
            ConflictPolicy.Skip => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = direction,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictSkipped,
                ConflictPolicyName = "skip",
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
        bool sourceWins = SourceWinsLastWriteWins(examined, direction);

        return sourceWins switch
        {
            true when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = SourceToDestination,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictWinner,
                WinningDirection = SourceToDestination,
                ConflictPolicyName = "last-write-wins",
            },
            true => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = DestinationToSource,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictNewerCannotUpdate,
                WinningDirection = SourceToDestination,
                ConflictPolicyName = "last-write-wins",
            },
            false when direction == SourceToDestination => new SyncAction<TItem>
            {
                Kind = SyncActionKind.Skip,
                Direction = SourceToDestination,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictNewerCannotUpdate,
                WinningDirection = DestinationToSource,
                ConflictPolicyName = "last-write-wins",
            },
            false => new SyncAction<TItem>
            {
                Kind = Update,
                Direction = DestinationToSource,
                Link = examined,
                ReasonKind = SyncActionReasonKind.ConflictWinner,
                WinningDirection = DestinationToSource,
                ConflictPolicyName = "last-write-wins",
            }
        };
    }

    /// <summary>
    /// Determines whether the source item wins a last-write-wins comparison.
    /// </summary>
    /// <remarks>
    /// Equal or absent modification timestamps use <paramref name="tieBreakDirection"/> as the
    /// deterministic tie-breaker. A source timestamp also wins when the destination has none.
    /// </remarks>
    private static bool SourceWinsLastWriteWins<TItem>(
        ExaminedLink<TItem> examined,
        SyncDirection tieBreakDirection) where TItem : CanonicalItem
    {
        var sourceLastModified = examined.SourceItem?.Provenance.LastModified;
        var destinationLastModified = examined.DestinationItem?.Provenance.LastModified;

        return sourceLastModified > destinationLastModified
               || (sourceLastModified == destinationLastModified && tieBreakDirection == SourceToDestination)
               || (sourceLastModified is not null && destinationLastModified is null);
    }

    /// <summary>
    /// Classifies an ordinary update without changing the planned operation.
    /// </summary>
    /// <remarks>
    /// Full scans have no delta interval to describe as an update, and force should be visible
    /// only when it overrides the otherwise-identical-content no-op. A force run with different
    /// content still reports the genuine difference as newer.
    /// </remarks>
    private static SyncActionReasonKind GetUpdateReason(JobOptions jobOptions, bool contentIdentical) =>
        jobOptions.Force && contentIdentical
            ? SyncActionReasonKind.Forced
            : jobOptions.Full || jobOptions.Force
                ? SyncActionReasonKind.Newer
                : SyncActionReasonKind.Default;

    /// <summary>
    /// Classifies a successful force-mode write without changing the policy-selected action.
    /// </summary>
    /// <remarks>
    /// Force treats both sides as changed for planning, which can synthesize a conflict around an
    /// item that was already known to be newer on only one side. Preserve that action decision,
    /// but report the actual newer side rather than a policy conflict when it is the selected
    /// writer. Identical content is described as forced only when timestamps also agree; a
    /// timestamp difference remains meaningful to last-write-wins conflict resolution.
    /// </remarks>
    private static SyncAction<TItem>? ApplyForceReason<TItem>(
        SyncAction<TItem>? action,
        bool force,
        bool contentIdentical,
        bool modificationTimestampsEqual,
        bool sourceModified,
        bool destinationModified) where TItem : CanonicalItem
    {
        if (!force || action is not { Kind: Update })
        {
            return action;
        }

        if (contentIdentical && modificationTimestampsEqual)
        {
            action.ReasonKind = SyncActionReasonKind.Forced;
            return action;
        }

        bool originWasModified = action.Direction == SourceToDestination
            ? sourceModified && !destinationModified
            : destinationModified && !sourceModified;
        if (originWasModified)
        {
            action.ReasonKind = SyncActionReasonKind.Newer;
        }

        return action;
    }
}
