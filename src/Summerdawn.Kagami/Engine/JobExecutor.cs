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

    public async Task<JobExecutionResult> ExecuteAsync<TItem>(Job<TItem> job, bool whatIf = false, CancellationToken cancellationToken = default) where TItem : CanonicalItem
    {
        string holderId = Guid.NewGuid().ToString("N");
        bool leaseAcquired = await leaseRepo.TryAcquireAsync(job.Key, holderId, LeaseDuration, cancellationToken);
        if (!leaseAcquired)
        {
            logger.LogWarning("Job {JobKey} is already running; skipping", job.Key);
            return new JobExecutionResult { JobKey = job.Key, Skipped = true, SkipReason = "Lease already held" };
        }

        try
        {
            return await RunJobAsync(job, whatIf, cancellationToken);
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
    private async Task<JobExecutionResult> RunJobAsync<TItem>(Job<TItem> job, bool whatIf, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        logger.LogInformation("Starting job {job.Key} (whatIf={WhatIf}, force={Force})", job.Key, whatIf, job.Options.Force);

        var sourceConnector = job.SourceConnector;
        var destinationConnector = job.DestinationConnector;

        var filter = CreateFilter<TItem>(job.Options.Filter);

        await sourceConnector.AuthenticateAsync(cancellationToken);
        await destinationConnector.AuthenticateAsync(cancellationToken);

        var existingLinks = await linkStateRepo.GetByPartitionAsync(job.PartitionKey, cancellationToken);
        JobExecutionResult result = new() { JobKey = job.Key };

        string filterScope = job.Options.Filter ?? string.Empty;

        // --- Determine cursors and effective run mode ---
        var sourceCursorState = await cursorRepo.GetCursorAsync(job.Key, job.Options.Source, cancellationToken);
        string? sourceCursor = GetApplicableCursor(job.Key, job.Options.Source, sourceCursorState, filterScope);

        var destinationCursorState = await cursorRepo.GetCursorAsync(job.Key, job.Options.Destination, cancellationToken);
        string? destinationCursor = GetApplicableCursor(job.Key, job.Options.Destination, destinationCursorState, filterScope);

        // A run is a delta run only when usable cursors are available for both sides and neither
        // Full nor Force is set.  Any other combination is treated as a full scan so the planner
        // can use authoritative absence semantics.
        bool isDeltaRun = !job.Options.Full && !job.Options.Force
            && sourceCursor != null && destinationCursor != null;

        // When this is not a delta run (first sync, scope changed, or explicit Full/Force), set
        // Full=true on the job options so the examiner treats absent items as
        // deleted/out-of-scope rather than implicitly unchanged.
        bool shouldRestoreFullFlag = !job.Options.Full && !isDeltaRun;
        if (shouldRestoreFullFlag)
        {
            job.Options.Full = true;
        }

        // --- Single read pass per connector (planning + cursor advancement) ---
        var sourcePageSet = await ReadAllPagesAsync(sourceConnector, job.Options.Source, sourceCursor, job.Options.Full || job.Options.Force, cancellationToken);
        var destinationPageSet = await ReadAllPagesAsync(destinationConnector, job.Options.Destination, destinationCursor, job.Options.Full || job.Options.Force, cancellationToken);

        // --- Plan actions using raw (unfiltered) items + filter ---
        // The filter is forwarded to LinkExaminer so it can detect items that moved out of scope.
        IReadOnlyList<SyncAction<TItem>> actions;
        try
        {
            actions = planner.PlanActions(job.Options, sourcePageSet.Items, destinationPageSet.Items, existingLinks, filter: filter);
        }
        finally
        {
            if (shouldRestoreFullFlag)
            {
                job.Options.Full = false;
            }
        }

        var actionsSkip = actions.Where(a => a.Kind == Skip).ToList();
        var actionsToDestination = actions.Where(a => a.Direction == SourceToDestination).Except(actionsSkip).ToList();
        var actionsToSource = actions.Where(a => a.Direction == DestinationToSource).Except(actionsSkip).ToList();

        logger.LogInformation("Job {job.Key}: {CountDestination} actions targeting destination, {CountSource} targeting source, {CountSkip} skip", job.Key, actionsToDestination.Count, actionsToSource.Count, actionsSkip.Count);
        result.ActionsPlanned += actionsToSource.Count + actionsToDestination.Count;

        if (whatIf)
        {
            syncActionExecutor.LogPlannedActions(job.Key, actions);
        }

        if (!whatIf)
        {
            // --- Apply source→destination ---
            await syncActionExecutor.ApplyActionsAsync(actionsToDestination, direction: SourceToDestination, job: job, existingLinks: existingLinks, cancellationToken: cancellationToken);

            // Refresh link state so that IDs created in the previous pass are visible when
            // applying destination→source actions.
            existingLinks = await linkStateRepo.GetByPartitionAsync(job.PartitionKey, cancellationToken);

            // --- Apply destination→source ---
            await syncActionExecutor.ApplyActionsAsync(actionsToSource, direction: DestinationToSource, job: job, existingLinks: existingLinks, cancellationToken: cancellationToken);

            // --- Record links for matched-but-unchanged pairs ---
            // Skip actions that carry both sides (i.e. a matched pair whose content was identical)
            // still need a link row so subsequent runs can track versions correctly.
            var skipsWithMatches = actionsSkip.Where(a => a.Item is not null && a.MatchedTargetItem is not null).ToList();
            if (skipsWithMatches.Count > 0)
            {
                existingLinks = await linkStateRepo.GetByPartitionAsync(job.PartitionKey, cancellationToken);

                var destinationSkipsWithMatches = skipsWithMatches
                    .Where(a => a.Direction == SourceToDestination)
                    .ToList();
                if (destinationSkipsWithMatches.Count > 0)
                {
                    await RecordUnchangedLinksAsync(job.PartitionKey, destinationSkipsWithMatches, existingLinks, SourceToDestination, cancellationToken);
                }

                var sourceSkipsWithMatches = skipsWithMatches
                    .Where(a => a.Direction == DestinationToSource)
                    .ToList();
                if (sourceSkipsWithMatches.Count > 0)
                {
                    await RecordUnchangedLinksAsync(job.PartitionKey, sourceSkipsWithMatches, existingLinks, DestinationToSource, cancellationToken);
                }
            }

            // --- Persist cursors ---
            if (sourcePageSet.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(job.Key, job.Options.Source, filterScope, sourcePageSet.Cursor, cancellationToken);
            }

            if (destinationPageSet.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(job.Key, job.Options.Destination, filterScope, destinationPageSet.Cursor, cancellationToken);
            }
        }

        result.Succeeded = true;
        logger.LogInformation("Job {job.Key} completed (whatIf={WhatIf}, actionsPlanned={Count})", job.Key, whatIf, result.ActionsPlanned);
        return result;
    }

    private static IFilter<TItem>? CreateFilter<TItem>(string? filterScope) where TItem : CanonicalItem
    {
        if (string.IsNullOrWhiteSpace(filterScope))
        {
            return null;
        }

        if (typeof(TItem) == typeof(CanonicalContact))
        {
            return (IFilter<TItem>?)(object?)ContactFilter.Parse(filterScope);
        }

        throw new NotSupportedException($"Filters are not supported for item type '{typeof(TItem).Name}'.");
    }

    /// <summary>
    /// Reads all incremental pages for an endpoint, starting from <paramref name="cursor"/> when
    /// available, or from the initial page when cursor is absent or <paramref name="force"/> is set.
    /// </summary>
    private async Task<PageSet<TItem>> ReadAllPagesAsync<TItem>(
        IConnector<TItem> connector,
        string endpointName,
        string? cursor,
        bool force,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        IncrementalPage<TItem> page;
        if (cursor is null || force)
        {
            if (force && cursor is not null)
            {
                logger.LogInformation("Force flag set for {Endpoint}; performing full re-sync (ignoring cursor)", endpointName);
            }
            else
            {
                logger.LogInformation("No cursor for {Endpoint}, performing initial full sync", endpointName);
            }

            page = await connector.GetInitialPageAsync(cancellationToken);
        }
        else
        {
            page = await connector.GetIncrementalPageAsync(cursor, cancellationToken);
        }

        List<TItem> items = [.. page.Items];
        string? finalCursor = page.NextCursor;

        while (page.HasMore)
        {
            if (page.NextCursor is null)
            {
                throw new InvalidOperationException($"Connector returned HasMore=true without a cursor for endpoint '{endpointName}'.");
            }

            page = await connector.GetIncrementalPageAsync(page.NextCursor, cancellationToken);
            items.AddRange(page.Items);
            finalCursor = page.NextCursor;
        }

        return new PageSet<TItem>(items, finalCursor);
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
    /// Records or updates link rows for Skip actions where both matched items were already
    /// content-identical, so no write action was emitted by the planner.
    /// </summary>
    /// <remarks>
    /// Ensures that the link table always reflects the latest observed IDs and hashes even when
    /// nothing changed, so subsequent runs can short-circuit correctly via <c>HasChanged</c>.
    /// </remarks>
    private async Task RecordUnchangedLinksAsync<TItem>(
        string partitionKey,
        IReadOnlyList<SyncAction<TItem>> unchangedActions,
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncDirection direction,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        foreach (var action in unchangedActions)
        {
            if (action.Item is null || action.MatchedTargetItem is null)
            {
                continue;
            }

            var link = FindLinkForUnchangedRecord(existingLinks, direction, action.Item.Provenance.ProviderId)
                ?? new LinkStateRow
                {
                    PartitionKey = partitionKey,
                    OriginSide = direction == SourceToDestination ? "Source" : "Destination",
                };

            link.SourceId = direction == SourceToDestination ? action.Item.Provenance.ProviderId : action.MatchedTargetItem.Provenance.ProviderId;
            link.DestinationId = direction == SourceToDestination ? action.MatchedTargetItem.Provenance.ProviderId : action.Item.Provenance.ProviderId;
            link.SourceVersion = direction == SourceToDestination ? action.Item.Provenance.Version : action.MatchedTargetItem.Provenance.Version;
            link.DestinationVersion = direction == SourceToDestination ? action.MatchedTargetItem.Provenance.Version : action.Item.Provenance.Version;
            link.SourceHash = direction == SourceToDestination ? action.Item.Provenance.ContentHash : action.MatchedTargetItem.Provenance.ContentHash;
            link.DestinationHash = direction == SourceToDestination ? action.MatchedTargetItem.Provenance.ContentHash : action.Item.Provenance.ContentHash;
            link.LastSyncedAt = DateTimeOffset.UtcNow;
            link.LastSyncResult = "unchanged";
            await linkStateRepo.UpsertAsync(link, cancellationToken);
        }
    }

    /// <summary>
    /// Finds the persisted link row for a skip/unchanged action, or returns <c>null</c> when no
    /// link yet exists.
    /// </summary>
    private static LinkStateRow? FindLinkForUnchangedRecord(
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncDirection direction,
        string providerId)
    {
        return direction == SourceToDestination
            ? existingLinks.FirstOrDefault(link => link.SourceId == providerId)
            : existingLinks.FirstOrDefault(link => link.DestinationId == providerId);
    }

    /// <summary>
    /// Represents a collected set of paged items and the final cursor to persist for the next run.
    /// </summary>
    private sealed record PageSet<TItem>(IReadOnlyList<TItem> Items, string? Cursor) where TItem : CanonicalItem;
}
