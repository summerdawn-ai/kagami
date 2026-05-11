using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Executes a single sync job: acquires a lease, reads both sides, plans actions,
/// and applies them.
/// </summary>
/// <remarks>
/// Responsible for orchestration: authentication, reading current snapshots, reading
/// incremental pages and cursors, planning actions, partitioning actions by direction and
/// skip, invoking <see cref="SyncActionExecutor"/>, recording unchanged links, and
/// committing cursors only on a clean (non-faulted) run.
/// </remarks>
public sealed class JobExecutor(
    SyncActionPlanner planner,
    LinkStateRepository linkStateRepo,
    EndpointCursorRepository cursorRepo,
    LeaseRepository leaseRepo,
    SyncActionExecutor syncActionExecutor,
    ILogger<JobExecutor> logger)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Executes a sync job.
    /// </summary>
    /// <remarks>
    /// Acquires a per-job lease before running; if another instance already holds the lease the
    /// job is skipped and <see cref="JobExecutionResult.Skipped"/> is set to <c>true</c>.
    /// The lease is always released in a <c>finally</c> block.
    /// </remarks>
    public Task<JobExecutionResult> ExecuteJobAsync<TItem>(Job<TItem> job, bool whatIf = false, CancellationToken cancellationToken = default) where TItem : CanonicalItem =>
        ExecuteJobAsync(job, whatIf ? JobExecutionFlags.WhatIf : JobExecutionFlags.None, cancellationToken);

    /// <summary>
    /// Executes a sync job.
    /// </summary>
    public async Task<JobExecutionResult> ExecuteJobAsync<TItem>(Job<TItem> job, JobExecutionFlags executionFlags, CancellationToken cancellationToken = default) where TItem : CanonicalItem
    {
        if (executionFlags.HasFlag(JobExecutionFlags.WhatIf) && executionFlags.HasFlag(JobExecutionFlags.Confirm))
        {
            throw new ArgumentException("What-if and confirm execution modes are mutually exclusive.", nameof(executionFlags));
        }

        string holderId = Guid.NewGuid().ToString("N");
        bool leaseAcquired = await leaseRepo.TryAcquireAsync(job.Key, holderId, LeaseDuration, cancellationToken);
        if (!leaseAcquired)
        {
            logger.LogWarning("Job {JobKey} is already running; skipping", job.Key);
            return new JobExecutionResult { JobKey = job.Key, Skipped = true, SkipReason = "Lease already held" };
        }

        try
        {
            return await RunJobAsync(job, executionFlags, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFaultedException ex)
        {
            logger.LogError(ex, "Job {JobKey} faulted; cursors will not advance", job.Key);
            return new JobExecutionResult { JobKey = job.Key, Succeeded = false, Error = ex.Message };
        }
        finally
        {
            await leaseRepo.ReleaseAsync(job.Key, holderId, cancellationToken);
        }
    }

    /// <summary>
    /// Authenticates both connectors, reads current state, plans actions, and applies them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method makes a single read pass per connector:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <term>Delta (cursor-based) pass</term>
    ///     <description>When a cursor is available and the filter scope has not changed, only
    ///     items that changed since the last cursor advance are fetched.  The planner treats
    ///     absence as "implicitly unchanged" rather than "deleted".</description>
    ///   </item>
    ///   <item>
    ///     <term>Full pass</term>
    ///     <description>Used on the first run (no cursor), when the filter scope changed, or when
    ///     <c>Full</c> or <c>Force</c> is set.  Fetches all current items; absence means the item
    ///     is no longer present.  The effective <see cref="JobOptions.Full"/> flag is set to
    ///     <c>true</c> when this mode is used so the planner applies full-scan semantics.</description>
    ///   </item>
    /// </list>
    /// <para>
    /// Raw (unfiltered) items are passed directly to the planner so that
    /// <see cref="LinkExaminer"/> can detect items that moved out of the filter scope and
    /// classify them as <see cref="SideActivity.MovedOutOfScope"/> rather than silently
    /// dropping them.
    /// </para>
    /// <para>
    /// Actions are applied source→destination first; link state is refreshed before the
    /// destination→source pass so that newly created destination IDs are visible.
    /// </para>
    /// </remarks>
    private async Task<JobExecutionResult> RunJobAsync<TItem>(Job<TItem> job, JobExecutionFlags executionFlags, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        bool whatIf = executionFlags.HasFlag(JobExecutionFlags.WhatIf);
        bool confirm = executionFlags.HasFlag(JobExecutionFlags.Confirm);
        logger.LogInformation("Starting job {job.Key} (whatIf={WhatIf}, confirm={Confirm}, force={Force})", job.Key, whatIf, confirm, job.Options.Force);

        var sourceConnector = job.SourceConnector;
        var destinationConnector = job.DestinationConnector;

        var filter = CreateFilter<TItem>(job.Options.Filter);

        await sourceConnector.AuthenticateAsync(cancellationToken);
        await destinationConnector.AuthenticateAsync(cancellationToken);

        // When NoPersistence is set, skip all DB reads and treat everything as a full scan.
        IReadOnlyList<LinkStateRow> existingLinks;
        string? sourceCursor = null;
        string? destinationCursor = null;

        string filterScope = job.Options.Filter ?? string.Empty;

        if (job.Options.NoPersistence)
        {
            existingLinks = [];
        }
        else
        {
            existingLinks = await linkStateRepo.GetByPartitionAsync(job.PartitionKey, cancellationToken);

            // --- Determine cursors and effective run mode ---
            var sourceCursorState = await cursorRepo.GetCursorAsync(job.Key, job.Options.SourceEndpointName, cancellationToken);
            sourceCursor = GetApplicableCursor(job.Key, job.Options.SourceEndpointName, sourceCursorState, filterScope);

            var destinationCursorState = await cursorRepo.GetCursorAsync(job.Key, job.Options.DestinationEndpointName, cancellationToken);
            destinationCursor = GetApplicableCursor(job.Key, job.Options.DestinationEndpointName, destinationCursorState, filterScope);
        }

        JobExecutionResult result = new() { JobKey = job.Key };

        // --- Single read pass per connector (planning + cursor advancement) ---
        (var sourceItemSet, var destinationItemSet, bool isDeltaRun) = await LoadItemSetsAsync(job, sourceCursor, destinationCursor, cancellationToken);

        // When this is not a delta run (first sync, scope changed, explicit Full/Force, or an
        // expired-cursor fallback), set Full=true on the job options so the examiner treats
        // absent items as deleted/out-of-scope rather than implicitly unchanged.
        bool shouldRestoreFullFlag = !job.Options.Full && !isDeltaRun;
        if (shouldRestoreFullFlag)
        {
            job.Options.Full = true;
        }

        // --- Plan actions using raw (unfiltered) items + filter ---
        // The filter is forwarded to LinkExaminer so it can detect items that moved out of scope.
        IReadOnlyList<SyncAction<TItem>> actions;
        try
        {
            actions = planner.PlanActions(job.Options, sourceItemSet.Items, destinationItemSet.Items, existingLinks, filter);
            StampActionEndpointNames(actions, job.Options);
        }
        finally
        {
            if (shouldRestoreFullFlag)
            {
                job.Options.Full = false;
            }
        }

        var actionsSkip = actions.Where(a => a.Kind == Skip).ToList();
        var actionsNone = actions.Where(a => a.Kind == None).ToList();
        var actionsToDestination = actions.Where(a => a.Direction == SourceToDestination).Except(actionsSkip).Except(actionsNone).ToList();
        var actionsToSource = actions.Where(a => a.Direction == DestinationToSource).Except(actionsSkip).Except(actionsNone).ToList();

        logger.LogInformation("Job {job.Key}: {CountDestination} actions targeting destination, {CountSource} targeting source, {CountSkip} skip, {CountNone} already in sync", job.Key, actionsToDestination.Count, actionsToSource.Count, actionsSkip.Count, actionsNone.Count);
        result.ActionsPlanned += actionsToSource.Count + actionsToDestination.Count;

        if (whatIf)
        {
            syncActionExecutor.LogPlannedActions(job.Key, actions);
        }
        else if (job.Options.NoPersistence)
        {
            // --- Apply source→destination ---
            confirm = await syncActionExecutor.ApplyActionsAsync(actionsToDestination, direction: SourceToDestination, job, confirm, cancellationToken);

            // --- Apply destination→source ---
            _ = await syncActionExecutor.ApplyActionsAsync(actionsToSource, direction: DestinationToSource, job, confirm, cancellationToken);
        }
        else
        {
            // --- Apply source→destination ---
            confirm = await syncActionExecutor.ApplyActionsAsync(actionsToDestination, direction: SourceToDestination, job, confirm, cancellationToken);

            // --- Apply destination→source ---
            _ = await syncActionExecutor.ApplyActionsAsync(actionsToSource, direction: DestinationToSource, job, confirm, cancellationToken);

            // --- Record links for content-identical pairs ---
            // None actions carry both sides with identical content; update the link row so
            // subsequent runs can short-circuit correctly via HasChanged.
            await RecordNoneActionsAsync(job.PartitionKey, actionsNone, cancellationToken);

            // --- Persist cursors ---
            if (sourceItemSet.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(job.Key, job.Options.SourceEndpointName, filterScope, sourceItemSet.Cursor, cancellationToken);
            }

            if (destinationItemSet.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(job.Key, job.Options.DestinationEndpointName, filterScope, destinationItemSet.Cursor, cancellationToken);
            }
        }

        result.Succeeded = true;
        logger.LogInformation("Job {job.Key} completed (whatIf={WhatIf}, confirm={Confirm}, actionsPlanned={Count})", job.Key, whatIf, executionFlags.HasFlag(JobExecutionFlags.Confirm), result.ActionsPlanned);
        return result;
    }

    private static void StampActionEndpointNames<TItem>(IEnumerable<SyncAction<TItem>> actions, JobOptions options) where TItem : CanonicalItem
    {
        foreach (var action in actions)
        {
            action.SourceEndpointName ??= options.SourceEndpointName;
            action.DestinationEndpointName ??= options.DestinationEndpointName;
        }
    }

    /// <summary>
    /// Reconstructs a typed filter from the scope string stored in <see cref="JobOptions.Filter"/>.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when no filter scope is configured. Throws when a filter scope is
    /// configured for an unsupported item type.
    /// </remarks>
    private static IFilter<TItem>? CreateFilter<TItem>(string? filterScope) where TItem : CanonicalItem
    {
        if (string.IsNullOrWhiteSpace(filterScope))
        {
            return null;
        }

        if (typeof(TItem) == typeof(CanonicalContact))
        {
            var filter = ContactFilter.Parse(filterScope)
                ?? throw new ArgumentException("Filter scope cannot be null, empty, or whitespace.", nameof(filterScope));

            return (IFilter<TItem>)(object)filter;
        }

        throw new NotSupportedException($"Filters are not supported for item type '{typeof(TItem).Name}'.");
    }

    private async Task<ItemSet<TItem>> LoadItemsAsync<TItem>(Job<TItem> job, IConnector<TItem> connector, string? cursor, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        // Use optimized endpoint if cursor not needed.
        if (job.Options.NoPersistence)
        {
            logger.LogInformation("No persistence for {Endpoint}; performing full load without cursors", connector.EndpointName);
            var items = await connector.GetAllItemsAsync(cancellationToken);
            return new ItemSet<TItem>(items, null);
        }

        if ((job.Options.Full || job.Options.Force) && cursor is not null)
        {
            logger.LogInformation("Full or force flag set for {Endpoint}; performing full load (ignoring cursor)", connector.EndpointName);

            // Ensure loading from beginning.
            cursor = null;
        }
        else if (cursor is null)
        {
            logger.LogInformation("No cursor for {Endpoint}, performing full load", connector.EndpointName);
        }

        return await connector.GetCursorItemsAsync(cursor, cancellationToken);
    }

    private async Task<(ItemSet<TItem> SourceItemSet, ItemSet<TItem> DestinationItemSet, bool IsDeltaRun)> LoadItemSetsAsync<TItem>(
        Job<TItem> job,
        string? sourceCursor,
        string? destinationCursor,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        // A run is a delta run only when usable cursors are available for both sides and neither
        // Full nor Force is set.  Any other combination is treated as a full scan so the planner
        // can use authoritative absence semantics.
        bool isDeltaRun = !job.Options.NoPersistence
                          && !job.Options.Full
                          && !job.Options.Force
                          && sourceCursor is not null
                          && destinationCursor is not null;

        if (!isDeltaRun && !job.Options.NoPersistence && (sourceCursor is not null || destinationCursor is not null))
        {
            logger.LogInformation(
                "Job {JobKey} will ignore stored cursors and perform a full load on both endpoints because this run is not a delta run.",
                job.Key);
        }

        string? effectiveSourceCursor = isDeltaRun ? sourceCursor : null;
        string? effectiveDestinationCursor = isDeltaRun ? destinationCursor : null;

        try
        {
            var sourceItemSet = await LoadItemsAsync(job, job.SourceConnector, effectiveSourceCursor, cancellationToken);
            var destinationItemSet = await LoadItemsAsync(job, job.DestinationConnector, effectiveDestinationCursor, cancellationToken);
            return (sourceItemSet, destinationItemSet, isDeltaRun);
        }
        catch (ExpiredCursorException ex) when (isDeltaRun)
        {
            logger.LogWarning(
                ex,
                "Job {JobKey} detected an expired cursor; clearing both cursors and retrying a full load.",
                job.Key);

            await cursorRepo.DeleteCursorAsync(job.Key, job.Options.SourceEndpointName, cancellationToken);
            await cursorRepo.DeleteCursorAsync(job.Key, job.Options.DestinationEndpointName, cancellationToken);

            var sourceItemSet = await LoadItemsAsync(job, job.SourceConnector, cursor: null, cancellationToken);
            var destinationItemSet = await LoadItemsAsync(job, job.DestinationConnector, cursor: null, cancellationToken);
            return (sourceItemSet, destinationItemSet, false);
        }
    }

    /// <summary>
    /// Returns the stored cursor for the given endpoint, or <c>null</c> when no cursor is
    /// available or when the stored scope differs from the current filter scope.
    /// </summary>
    /// <remarks>
    /// A scope mismatch means the filter expression has changed since the cursor was captured;
    /// using a stale cursor against a different scope would yield incorrect incremental results.
    /// </remarks>
    private string? GetApplicableCursor(
        string jobKey,
        string endpointName,
        EndpointCursorState? cursorState,
        string currentScope)
    {
        if (cursorState is null)
        {
            return null;
        }

        if (string.Equals(cursorState.Scope, currentScope, StringComparison.Ordinal))
        {
            return cursorState.Cursor;
        }

        logger.LogInformation(
            "Ignoring saved cursor for job {JobKey}, endpoint {EndpointName} because stored scope '{StoredScope}' differs from current scope '{CurrentScope}'",
            jobKey,
            endpointName,
            cursorState.Scope,
            currentScope);

        return null;
    }

    /// <summary>
    /// Records or updates link rows for <see cref="SyncActionKind.None"/> actions, where both
    /// sides were already carrying identical content and no write was required.
    /// </summary>
    /// <remarks>
    /// Ensures that the link table always reflects the latest observed IDs and hashes so
    /// subsequent runs can short-circuit correctly via <c>HasChanged</c>. Conflict-resolution
    /// <see cref="SyncActionKind.Skip"/> actions are intentionally excluded: those represent
    /// an unresolved disagreement, and omitting them causes <c>--full</c> resync to
    /// re-surface the conflict rather than silently ignoring it.
    /// </remarks>
    private async Task RecordNoneActionsAsync<TItem>(
        string partitionKey,
        IReadOnlyList<SyncAction<TItem>> unchangedActions,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        foreach (var action in unchangedActions)
        {
            var sourceItem = action.Link.SourceItem;
            var destinationItem = action.Link.DestinationItem;

            if (sourceItem is null || destinationItem is null)
            {
                continue;
            }

            var link = action.Link.PersistedState
                ?? new LinkStateRow
                {
                    PartitionKey = partitionKey,
                    OriginSide = action.Direction == SourceToDestination ? "Source" : "Destination",
                };

            link.SourceId = sourceItem.Provenance.ProviderId;
            link.DestinationId = destinationItem.Provenance.ProviderId;
            link.SourceVersion = sourceItem.Provenance.Version;
            link.DestinationVersion = destinationItem.Provenance.Version;
            link.SourceHash = sourceItem.Provenance.ContentHash;
            link.DestinationHash = destinationItem.Provenance.ContentHash;
            link.LastSyncedAt = DateTimeOffset.UtcNow;
            link.LastSyncResult = "unchanged";
            await linkStateRepo.UpsertAsync(link, cancellationToken);
        }
    }
}
