
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
    /// <param name="force">
    /// When <c>true</c>, bypasses the HasChanged short-circuit so that all in-scope contacts
    /// are re-evaluated and re-applied even if their version/hash has not changed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JobExecutionResult> ExecuteAsync(
        string jobKey,
        JobOptions jobOptions,
        IConnector sourceConnector,
        IConnector destinationConnector,
        bool whatIf = false,
        ContactFilter? filter = null,
        bool force = false,
        CancellationToken cancellationToken = default)
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
            return await RunJobAsync(jobKey, jobOptions, sourceConnector, destinationConnector, whatIf, filter, force, cancellationToken);
        }
        finally
        {
            await leaseRepo.ReleaseAsync(jobKey, holderId, cancellationToken);
        }
    }

    private async Task<JobExecutionResult> RunJobAsync(
        string jobKey,
        JobOptions jobOptions,
        IConnector sourceConnector,
        IConnector destinationConnector,
        bool whatIf,
        ContactFilter? filter,
        bool force,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting job {JobKey} (whatIf={WhatIf}, force={Force})", jobKey, whatIf, force);

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
        var sourcePageSet = await ReadAllPagesAsync(sourceConnector, jobOptions.Source, sourceCursor, force, cancellationToken);

        var destinationCursorState = await cursorRepo.GetCursorAsync(jobKey, jobOptions.Destination, cancellationToken);
        string? destinationCursor = GetApplicableCursor(jobKey, jobOptions.Destination, destinationCursorState, filterScope);
        var destinationPageSet = await ReadAllPagesAsync(destinationConnector, jobOptions.Destination, destinationCursor, force, cancellationToken);

        // --- Plan actions (single pass over full current state from both sides) ---
        var actions = planner.PlanActions(jobOptions, currentSourceItems, currentDestinationItems, existingLinks, force);

        var actionsToDestination = actions.Where(a => a.TargetSide == SyncSide.Destination).ToList();
        var actionsToSource = actions.Where(a => a.TargetSide == SyncSide.Source).ToList();

        logger.LogInformation("Job {JobKey}: {Count} actions targeting destination, {CountA} targeting source", jobKey, actionsToDestination.Count, actionsToSource.Count);
        result.ActionsPlanned += actions.Count;

        if (whatIf)
        {
            LogPlannedActions(jobKey, actions);
        }

        if (!whatIf)
        {
            await EnsureActionItemsLoadedAsync(actionsToDestination, cancellationToken);
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsToDestination, destinationConnector, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.Destination, cancellationToken);
            existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);

            await EnsureActionItemsLoadedAsync(actionsToSource, cancellationToken);
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsToSource, sourceConnector, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.Source, cancellationToken);

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

    private async Task ApplyActionsAsync(
        string jobKey,
        string entityType,
        IReadOnlyList<SyncAction> actions,
        IConnector targetConnector,
        LinkStateRepository linkStateRepository,
        OperationLogRepository operationLog,
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncSide updateSide,
        CancellationToken cancellationToken)
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
                            await operationLog.AppendAsync(jobKey, entityType, "create", created.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = new LinkStateRow
                            {
                                JobKey = jobKey,
                                EntityType = entityType,
                                SourceId = updateSide == SyncSide.Destination ? action.Item.SourceId : created.SourceId,
                                DestinationId = updateSide == SyncSide.Destination ? created.SourceId : action.Item.SourceId,
                                SourceVersion = updateSide == SyncSide.Destination ? action.Item.Version : created.Version,
                                DestinationVersion = updateSide == SyncSide.Destination ? created.Version : action.Item.Version,
                                SourceHash = updateSide == SyncSide.Destination ? action.Item.ContentHash : created.ContentHash,
                                DestinationHash = updateSide == SyncSide.Destination ? created.ContentHash : action.Item.ContentHash,
                                OriginSide = updateSide == SyncSide.Destination ? "Source" : "Destination",
                                LastSyncedAt = DateTimeOffset.UtcNow,
                                LastSyncResult = "created",
                            };
                            await linkStateRepository.UpsertAsync(link, cancellationToken);
                            break;
                        }

                    case SyncActionKind.Update when action.Item is not null:
                        {
                            var link = FindLinkForUpdate(existingLinks, updateSide, action.Item.SourceId);
                            if (link is not null && action.MatchedTargetItem is not null && IsDuplicateLinkAction(link, action, updateSide))
                            {
                                continue;
                            }

                            if (link is null)
                            {
                                if (action.MatchedTargetItem is null)
                                {
                                    continue;
                                }

                                var matchedTarget = CreateTargetItem(action.Item, null, updateSide, action.MatchedTargetItem);
                                var matchedUpdate = await targetConnector.UpdateItemAsync(matchedTarget, cancellationToken);
                                logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", jobKey, DescribeActionTarget(action), updateSide);
                                await operationLog.AppendAsync(jobKey, entityType, "update", matchedUpdate.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                                var matchedLink = new LinkStateRow
                                {
                                    JobKey = jobKey,
                                    EntityType = entityType,
                                    SourceId = updateSide == SyncSide.Destination ? action.Item.SourceId : matchedUpdate.SourceId,
                                    DestinationId = updateSide == SyncSide.Destination ? matchedUpdate.SourceId : action.Item.SourceId,
                                    SourceVersion = updateSide == SyncSide.Destination ? action.Item.Version : matchedUpdate.Version,
                                    DestinationVersion = updateSide == SyncSide.Destination ? matchedUpdate.Version : action.Item.Version,
                                    SourceHash = updateSide == SyncSide.Destination ? action.Item.ContentHash : matchedUpdate.ContentHash,
                                    DestinationHash = updateSide == SyncSide.Destination ? matchedUpdate.ContentHash : action.Item.ContentHash,
                                    OriginSide = updateSide == SyncSide.Destination ? "Source" : "Destination",
                                    LastSyncedAt = DateTimeOffset.UtcNow,
                                    LastSyncResult = "updated",
                                };
                                await linkStateRepository.UpsertAsync(matchedLink, cancellationToken);
                                break;
                            }

                            var targetItem = CreateTargetItem(action.Item, link, updateSide, action.MatchedTargetItem);
                            var updated = await targetConnector.UpdateItemAsync(targetItem, cancellationToken);
                            logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", jobKey, DescribeActionTarget(action), updateSide);
                            await operationLog.AppendAsync(jobKey, entityType, "update", updated.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                            if (updateSide == SyncSide.Destination)
                            {
                                link.SourceVersion = action.Item.Version;
                                link.SourceHash = action.Item.ContentHash;
                                link.DestinationVersion = updated.Version;
                                link.DestinationHash = updated.ContentHash;
                            }
                            else
                            {
                                link.DestinationVersion = action.Item.Version;
                                link.DestinationHash = action.Item.ContentHash;
                                link.SourceVersion = updated.Version;
                                link.SourceHash = updated.ContentHash;
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
                string itemId = action.Item?.SourceId ?? action.DeleteId ?? "unknown";
                logger.LogError(ex, "Job {JobKey}: failed to apply {Kind} action for item {ItemId} on side {Side}; skipping",
                    jobKey, action.Kind, itemId, updateSide);
            }
        }
    }

    private async Task<PageSet> ReadAllPagesAsync(
        IConnector connector,
        string endpointName,
        string? cursor,
        bool force,
        CancellationToken cancellationToken)
    {
        IncrementalPage page;
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

        List<CanonicalItem> items = [.. page.Items];
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

        return new PageSet(items, finalCursor);
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

    private async Task<PageSet> ReadCurrentPagesAsync(
        IConnector connector,
        string endpointName,
        CancellationToken cancellationToken)
    {
        var page = await connector.GetInitialPageAsync(cancellationToken);
        List<CanonicalItem> items = [.. page.Items];
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

        return new PageSet(items, finalCursor);
    }

    private void LogPlannedActions(string jobKey, IReadOnlyList<SyncAction> actions)
    {
        foreach (var action in actions.Where(a => a.Kind is SyncActionKind.Create or SyncActionKind.Update or SyncActionKind.Delete))
        {
            string verb = action.Kind switch
            {
                SyncActionKind.Create => "create",
                SyncActionKind.Update => "update",
                SyncActionKind.Delete => "delete",
                _ => action.Kind.ToString().ToLowerInvariant(),
            };

            logger.LogInformation(
                "What-if job {JobKey}: would {Verb} {Description} on side {TargetSide} ({Reason})",
                jobKey,
                verb,
                DescribeActionTarget(action),
                action.TargetSide,
                action.Reason ?? "no reason provided");
        }
    }

    private static string DescribeActionTarget(SyncAction action)
    {
        if (action.Item?.Payload is CanonicalContact contact)
        {
            string name = ContactName.GetName(contact);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = action.Item.SourceId;
            }

            return $"contact '{name}'";
        }

        if (action.Item is not null)
        {
            return $"item '{action.Item.SourceId}'";
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

    private static async Task EnsureActionItemsLoadedAsync(IReadOnlyList<SyncAction> actions, CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            if (action.Kind is SyncActionKind.Create or SyncActionKind.Update && action.Item is not null)
            {
                await ContactPhotoLoader.EnsureLoadedAsync(action.Item, cancellationToken);
            }
        }
    }

    private static CanonicalItem CreateTargetItem(
        CanonicalItem sourceItem,
        LinkStateRow? link,
        SyncSide updateSide,
        CanonicalItem? matchedTargetItem) =>
        new()
        {
            EntityType = sourceItem.EntityType,
            Payload = sourceItem.Payload,
            SourceId = matchedTargetItem?.SourceId ?? (updateSide == SyncSide.Destination ? link!.DestinationId! : link!.SourceId),
            Version = matchedTargetItem?.Version ?? (updateSide == SyncSide.Destination ? link!.DestinationVersion : link!.SourceVersion),
            ContentHash = sourceItem.ContentHash,
            IsDeleted = sourceItem.IsDeleted,
            Metadata = new Dictionary<string, string>(sourceItem.Metadata),
        };

    private static bool IsDuplicateLinkAction(LinkStateRow link, SyncAction action, SyncSide updateSide) =>
        action.Item is not null
        && action.MatchedTargetItem is not null
        && (updateSide == SyncSide.Destination
            ? link.SourceId == action.Item.SourceId && link.DestinationId == action.MatchedTargetItem.SourceId
            : link.DestinationId == action.Item.SourceId && link.SourceId == action.MatchedTargetItem.SourceId);

    private sealed record PageSet(IReadOnlyList<CanonicalItem> Items, string? Cursor);
}
