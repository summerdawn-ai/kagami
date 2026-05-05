using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;
/// <summary>
/// Executes a single sync job using two connectors and the planner.
/// </summary>
public sealed class JobExecutor(
    Planner planner,
    LinkStateRepository linkStateRepo,
    EndpointCursorRepository cursorRepo,
    OperationLogRepository opLog,
    LeaseRepository leaseRepo,
    ILogger<JobExecutor> logger)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Executes a sync job. If <paramref name="whatIf"/> is true, no writes are performed.
    /// </summary>
    /// <param name="jobKey">The job key used for lease and state tracking.</param>
    /// <param name="jobOptions">The job configuration.</param>
    /// <param name="sourceConnector">Connector for source side.</param>
    /// <param name="destinationConnector">Connector for destination side.</param>
    /// <param name="whatIf">When <c>true</c>, no writes are performed; planned actions are logged.</param>
    /// <param name="filter">
    /// Optional in-memory contact filter. Only items matching the filter are included in planning;
    /// items not matching the filter are left completely untouched on both sides.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JobExecutionResult> ExecuteAsync<TItem>(
        string jobKey,
        JobOptions jobOptions,
        IConnector<TItem> sourceConnector,
        IConnector<TItem> destinationConnector,
        bool whatIf = false,
        IFilter<TItem>? filter = null,
        CancellationToken cancellationToken = default) where TItem : CanonicalItem
    {
        string holderId = Guid.NewGuid().ToString("N");
        bool leaseAcquired = await leaseRepo.TryAcquireAsync(jobKey, holderId, LeaseDuration, cancellationToken);
        if (!leaseAcquired)
        {
            logger.LogWarning("Job {JobKey} is already running; skipping", jobKey);
            return new JobExecutionResult { JobKey = jobKey, Skipped = true, SkipReason = "Lease already held" };
        }

        try
        {
            return await RunJobAsync(jobKey, jobOptions, sourceConnector, destinationConnector, whatIf, filter, cancellationToken);
        }
        finally
        {
            await leaseRepo.ReleaseAsync(jobKey, holderId, cancellationToken);
        }
    }

    private async Task<JobExecutionResult> RunJobAsync<TItem>(
        string jobKey,
        JobOptions jobOptions,
        IConnector<TItem> sourceConnector,
        IConnector<TItem> destinationConnector,
        bool whatIf,
        IFilter<TItem>? filter,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        logger.LogInformation("Starting job {JobKey} (whatIf={WhatIf}, force={Force})", jobKey, whatIf, jobOptions.Force);

        await sourceConnector.AuthenticateAsync(cancellationToken);
        await destinationConnector.AuthenticateAsync(cancellationToken);

        var existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);
        JobExecutionResult result = new() { JobKey = jobKey };
        string filterScope = filter?.Scope ?? string.Empty;

        // --- Read current snapshots from both sides ---
        var currentSourcePageSet = await ReadCurrentPagesAsync(sourceConnector, jobOptions.Source, cancellationToken);
        var currentSourceItems = filter is not null ? filter.Apply(currentSourcePageSet.Items) : currentSourcePageSet.Items;

        var currentDestinationPageSet = await ReadCurrentPagesAsync(destinationConnector, jobOptions.Destination, cancellationToken);
        var currentDestinationItems = filter is not null ? filter.Apply(currentDestinationPageSet.Items) : currentDestinationPageSet.Items;

        // --- Poll incremental changes (for cursor tracking) ---
        var sourceCursorState = await cursorRepo.GetCursorAsync(jobKey, jobOptions.Source, cancellationToken);
        string? sourceCursor = GetApplicableCursor(jobKey, jobOptions.Source, sourceCursorState, filterScope);
        var sourcePageSet = await ReadAllPagesAsync(sourceConnector, jobOptions.Source, sourceCursor, jobOptions.Full || jobOptions.Force, cancellationToken);

        var destinationCursorState = await cursorRepo.GetCursorAsync(jobKey, jobOptions.Destination, cancellationToken);
        string? destinationCursor = GetApplicableCursor(jobKey, jobOptions.Destination, destinationCursorState, filterScope);
        var destinationPageSet = await ReadAllPagesAsync(destinationConnector, jobOptions.Destination, destinationCursor, jobOptions.Full || jobOptions.Force, cancellationToken);

        // --- Plan actions (single pass over full current state from both sides) ---
        var actions = planner.PlanActions(jobOptions, currentSourceItems, currentDestinationItems, existingLinks);

        var actionsSkip = actions.Where(a => a.Kind == SyncActionKind.Skip).ToList();
        var actionsToDestination = actions.Where(a => a.TargetSide == SyncSide.Destination).Except(actionsSkip).ToList();
        var actionsToSource = actions.Where(a => a.TargetSide == SyncSide.Source).Except(actionsSkip).ToList();

        logger.LogInformation("Job {JobKey}: {CountDestination} actions targeting destination, {CountSource} targeting source, {CountSkip} skip", jobKey, actionsToDestination.Count, actionsToSource.Count, actionsSkip.Count);
        result.ActionsPlanned += actionsToSource.Count + actionsToDestination.Count;

        if (whatIf)
        {
            LogPlannedActions(jobKey, actions);
        }

        if (!whatIf)
        {
            await EnsureActionItemsLoadedAsync(actionsToDestination, cancellationToken);
            await ApplyActionsAsync<TItem>(jobKey, jobOptions.EntityType, actionsToDestination, destinationConnector, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.Destination, cancellationToken);
            existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);

            await EnsureActionItemsLoadedAsync(actionsToSource, cancellationToken);
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsToSource, sourceConnector, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.Source, cancellationToken);

            var skipsWithMatches = actionsSkip.Where(a => a.Item is not null && a.MatchedTargetItem is not null).ToList();
            if (skipsWithMatches.Count > 0)
            {
                existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);

                var destinationSkipsWithMatches = skipsWithMatches
                    .Where(a => a.TargetSide == SyncSide.Destination)
                    .ToList();
                if (destinationSkipsWithMatches.Count > 0)
                {
                    await RecordUnchangedLinksAsync(jobKey, jobOptions.EntityType, destinationSkipsWithMatches, existingLinks, SyncSide.Destination, cancellationToken);
                }

                var sourceSkipsWithMatches = skipsWithMatches
                    .Where(a => a.TargetSide == SyncSide.Source)
                    .ToList();
                if (sourceSkipsWithMatches.Count > 0)
                {
                    await RecordUnchangedLinksAsync(jobKey, jobOptions.EntityType, sourceSkipsWithMatches, existingLinks, SyncSide.Source, cancellationToken);
                }
            }

            if (sourcePageSet.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(jobKey, jobOptions.Source, filterScope, sourcePageSet.Cursor, cancellationToken);
            }

            if (destinationPageSet.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(jobKey, jobOptions.Destination, filterScope, destinationPageSet.Cursor, cancellationToken);
            }
        }

        result.Succeeded = true;
        logger.LogInformation("Job {JobKey} completed (whatIf={WhatIf}, actionsPlanned={Count})", jobKey, whatIf, result.ActionsPlanned);
        return result;
    }

    private async Task ApplyActionsAsync<TItem>(
        string jobKey,
        string entityType,
        IReadOnlyList<SyncAction<TItem>> actions,
        IConnector<TItem> targetConnector,
        LinkStateRepository linkStateRepository,
        OperationLogRepository operationLog,
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncSide updateSide,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        var linksBySourceId = existingLinks.ToDictionary(l => l.SourceId);
        var linksByDestinationId = existingLinks
            .Where(l => l.DestinationId != null)
            .ToDictionary(l => l.DestinationId!);

        foreach (var action in actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.Create when action.Item is not null:
                        {
                            var created = await targetConnector.CreateItemAsync(action.Item, cancellationToken);
                            logger.LogInformation("Job {JobKey}: created {Description} on side {Side}", jobKey, DescribeActionTarget(action), updateSide);
                            await operationLog.AppendAsync(jobKey, entityType, "create", created.Provenance.ProviderId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = new LinkStateRow
                            {
                                JobKey = jobKey,
                                EntityType = entityType,
                                SourceId = updateSide == SyncSide.Destination ? action.Item.Provenance.ProviderId : created.Provenance.ProviderId,
                                DestinationId = updateSide == SyncSide.Destination ? created.Provenance.ProviderId : action.Item.Provenance.ProviderId,
                                SourceVersion = updateSide == SyncSide.Destination ? action.Item.Provenance.Version : created.Provenance.Version,
                                DestinationVersion = updateSide == SyncSide.Destination ? created.Provenance.Version : action.Item.Provenance.Version,
                                SourceHash = updateSide == SyncSide.Destination ? action.Item.Provenance.ContentHash : created.Provenance.ContentHash,
                                DestinationHash = updateSide == SyncSide.Destination ? created.Provenance.ContentHash : action.Item.Provenance.ContentHash,
                                OriginSide = updateSide == SyncSide.Destination ? "Source" : "Destination",
                                LastSyncedAt = DateTimeOffset.UtcNow,
                                LastSyncResult = "created",
                            };
                            await linkStateRepository.UpsertAsync(link, cancellationToken);
                            break;
                        }

                    case SyncActionKind.Update when action.Item is not null:
                        {
                            var link = FindLinkForUpdate(existingLinks, updateSide, action.Item.Provenance.ProviderId);
                            if (link is null)
                            {
                                if (action.MatchedTargetItem is null)
                                {
                                    continue;
                                }

                                var matchedTarget = CreateTargetItem(action.Item, null, updateSide, action.MatchedTargetItem);

                                DetachPhotoIfUnchanged(action, matchedTarget);

                                var matchedUpdate = await targetConnector.UpdateItemAsync(matchedTarget, cancellationToken);
                                logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", jobKey, DescribeActionTarget(action), updateSide);
                                await operationLog.AppendAsync(jobKey, entityType, "update", matchedUpdate.Provenance.ProviderId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                                var matchedLink = new LinkStateRow
                                {
                                    JobKey = jobKey,
                                    EntityType = entityType,
                                    SourceId = updateSide == SyncSide.Destination ? action.Item.Provenance.ProviderId : matchedUpdate.Provenance.ProviderId,
                                    DestinationId = updateSide == SyncSide.Destination ? matchedUpdate.Provenance.ProviderId : action.Item.Provenance.ProviderId,
                                    SourceVersion = updateSide == SyncSide.Destination ? action.Item.Provenance.Version : matchedUpdate.Provenance.Version,
                                    DestinationVersion = updateSide == SyncSide.Destination ? matchedUpdate.Provenance.Version : action.Item.Provenance.Version,
                                    SourceHash = updateSide == SyncSide.Destination ? action.Item.Provenance.ContentHash : matchedUpdate.Provenance.ContentHash,
                                    DestinationHash = updateSide == SyncSide.Destination ? matchedUpdate.Provenance.ContentHash : action.Item.Provenance.ContentHash,
                                    OriginSide = updateSide == SyncSide.Destination ? "Source" : "Destination",
                                    LastSyncedAt = DateTimeOffset.UtcNow,
                                    LastSyncResult = "updated",
                                };
                                await linkStateRepository.UpsertAsync(matchedLink, cancellationToken);
                                break;
                            }

                            var targetItem = CreateTargetItem(action.Item, link, updateSide, action.MatchedTargetItem);

                            DetachPhotoIfUnchanged(action, targetItem);

                            var updated = await targetConnector.UpdateItemAsync(targetItem, cancellationToken);
                            logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", jobKey, DescribeActionTarget(action), updateSide);
                            await operationLog.AppendAsync(jobKey, entityType, "update", updated.Provenance.ProviderId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                            if (updateSide == SyncSide.Destination)
                            {
                                link.SourceVersion = action.Item.Provenance.Version;
                                link.SourceHash = action.Item.Provenance.ContentHash;
                                link.DestinationVersion = updated.Provenance.Version;
                                link.DestinationHash = updated.Provenance.ContentHash;
                            }
                            else
                            {
                                link.DestinationVersion = action.Item.Provenance.Version;
                                link.DestinationHash = action.Item.Provenance.ContentHash;
                                link.SourceVersion = updated.Provenance.Version;
                                link.SourceHash = updated.Provenance.ContentHash;
                            }

                            link.LastSyncedAt = DateTimeOffset.UtcNow;
                            link.LastSyncResult = "updated";
                            await linkStateRepository.UpsertAsync(link, cancellationToken);

                            break;
                        }

                    case SyncActionKind.Delete when action.DeleteId is not null:
                        {
                            await targetConnector.DeleteItemAsync(action.DeleteId, cancellationToken);
                            logger.LogInformation("Job {JobKey}: deleted item {ItemId} on side {Side}", jobKey, action.DeleteId, updateSide);
                            await operationLog.AppendAsync(jobKey, entityType, "delete", action.DeleteId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = updateSide == SyncSide.Destination
                                ? linksByDestinationId.GetValueOrDefault(action.DeleteId)
                                : linksBySourceId.GetValueOrDefault(action.DeleteId);

                            if (link is not null)
                            {
                                if (updateSide == SyncSide.Destination)
                                {
                                    link.DestinationDeleted = true;
                                }
                                else
                                {
                                    link.SourceDeleted = true;
                                }

                                link.LastSyncedAt = DateTimeOffset.UtcNow;
                                link.LastSyncResult = "deleted";
                                await linkStateRepository.UpsertAsync(link, cancellationToken);
                            }

                            break;
                        }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string itemId = action.Item?.Provenance.ProviderId ?? action.DeleteId ?? "unknown";
                string operation = action.Kind switch
                {
                    SyncActionKind.Create => "create",
                    SyncActionKind.Update => "update",
                    SyncActionKind.Delete => "delete",
                    _ => action.Kind.ToString().ToLowerInvariant()
                };

                logger.LogError(ex, "Job {JobKey}: failed to apply {Kind} action for item {ItemId} on side {Side}; skipping",
                    jobKey, action.Kind, itemId, updateSide);
                await operationLog.AppendAsync(jobKey, entityType, operation, itemId, updateSide.ToString(), "error", cancellationToken: cancellationToken);
            }
        }
    }

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

    private async Task<PageSet<TItem>> ReadCurrentPagesAsync<TItem>(
        IConnector<TItem> connector,
        string endpointName,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        var page = await connector.GetInitialPageAsync(cancellationToken);
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

    private void LogPlannedActions<TItem>(string jobKey, IReadOnlyList<SyncAction<TItem>> actions) where TItem : CanonicalItem
    {
        foreach (var action in actions)
        {
            string verb = action.Kind.ToString().ToLowerInvariant();
            logger.LogInformation(
                "What-if job {JobKey}: would {Verb} {Description} on side {TargetSide} ({Reason})",
                jobKey,
                verb,
                DescribeActionTarget(action),
                action.TargetSide,
                action.Reason ?? "no reason provided");
        }
    }

    private static string DescribeActionTarget<TItem>(SyncAction<TItem> action) where TItem : CanonicalItem
    {
        if (action.Item is CanonicalContact contact)
        {
            string name = ContactName.GetName(contact);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = action.Item.Provenance.ProviderId;
            }

            return $"contact '{name}'";
        }

        if (action.Item is not null)
        {
            return $"item '{action.Item.Provenance.ProviderId}'";
        }

        return $"item '{action.DeleteId}'";
    }

    private static LinkStateRow? FindLinkForUpdate(
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncSide updateSide,
        string sourceId)
    {
        return updateSide == SyncSide.Destination
            ? existingLinks.FirstOrDefault(link => link.SourceId == sourceId)
            : existingLinks.FirstOrDefault(link => link.DestinationId == sourceId);
    }

    private static async Task EnsureActionItemsLoadedAsync<TItem>(IReadOnlyList<SyncAction<TItem>> actions, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        foreach (var action in actions)
        {
            if (action.Kind is SyncActionKind.Create or SyncActionKind.Update && action.Item is CanonicalContact contact)
            {
                await ContactPhotoLoader.EnsureLoadedAsync(contact, cancellationToken);
            }

            if (action.Kind == SyncActionKind.Update && action.MatchedTargetItem is CanonicalContact targetContact)
            {
                await ContactPhotoLoader.EnsureLoadedAsync(targetContact, cancellationToken);
            }
        }
    }

    private static TItem CreateTargetItem<TItem>(
        TItem sourceItem,
        LinkStateRow? link,
        SyncSide updateSide,
        CanonicalItem? matchedTargetItem) where TItem : CanonicalItem
    {
        var targetItem = sourceItem with
        {
            Provenance = new()
            {
                ProviderId = matchedTargetItem?.Provenance.ProviderId ??
                             (updateSide == SyncSide.Destination ? link!.DestinationId! : link!.SourceId),
                Version = matchedTargetItem?.Provenance.Version ??
                          (updateSide == SyncSide.Destination ? link!.DestinationVersion : link!.SourceVersion),
            }
        };

        return targetItem;
    }

    private static void DetachPhotoIfUnchanged<TItem>(SyncAction<TItem> action, TItem matchedTarget) where TItem : CanonicalItem
    {
        if (typeof(TItem) != typeof(CanonicalContact))
        {
            return;
        }

        if (ContactPhotoMetadata.PhotoHashesMatch(action.Item as CanonicalContact, action.MatchedTargetItem as CanonicalContact))
        {
            ContactPhotoMetadata.DetachPhoto((matchedTarget as CanonicalContact)!);
        }
    }

    /// <summary>
    /// Records or updates link rows for Skip actions where the content was already identical
    /// on both sides, so the planner did not emit an Update action.
    /// </summary>
    private async Task RecordUnchangedLinksAsync<TItem>(
        string jobKey,
        string entityType,
        IReadOnlyList<SyncAction<TItem>> unchangedActions,
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncSide updateSide,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        foreach (var action in unchangedActions)
        {
            if (action.Item is null || action.MatchedTargetItem is null)
            {
                continue;
            }

            var link = FindLinkForUpdate(existingLinks, updateSide, action.Item.Provenance.ProviderId)
                ?? new LinkStateRow
                {
                    JobKey = jobKey,
                    EntityType = entityType,
                    OriginSide = updateSide == SyncSide.Destination ? "Source" : "Destination",
                };

            link.SourceId = updateSide == SyncSide.Destination ? action.Item.Provenance.ProviderId : action.MatchedTargetItem.Provenance.ProviderId;
            link.DestinationId = updateSide == SyncSide.Destination ? action.MatchedTargetItem.Provenance.ProviderId : action.Item.Provenance.ProviderId;
            link.SourceVersion = updateSide == SyncSide.Destination ? action.Item.Provenance.Version : action.MatchedTargetItem.Provenance.Version;
            link.DestinationVersion = updateSide == SyncSide.Destination ? action.MatchedTargetItem.Provenance.Version : action.Item.Provenance.Version;
            link.SourceHash = updateSide == SyncSide.Destination ? action.Item.Provenance.ContentHash : action.MatchedTargetItem.Provenance.ContentHash;
            link.DestinationHash = updateSide == SyncSide.Destination ? action.MatchedTargetItem.Provenance.ContentHash : action.Item.Provenance.ContentHash;
            link.LastSyncedAt = DateTimeOffset.UtcNow;
            link.LastSyncResult = "unchanged";
            await linkStateRepo.UpsertAsync(link, cancellationToken);
        }
    }

    private sealed record PageSet<TItem>(IReadOnlyList<TItem> Items, string? Cursor) where TItem : CanonicalItem;
}
