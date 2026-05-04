using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

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
    /// <param name="job">The job to execute.</param>
    /// <param name="whatIf">When <c>true</c>, no writes are performed; planned actions are logged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
        finally
        {
            await leaseRepo.ReleaseAsync(job.Key, holderId, cancellationToken);
        }
    }

    private async Task<JobExecutionResult> RunJobAsync<TItem>(Job<TItem> job, bool whatIf, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        logger.LogInformation("Starting job {job.Key} (whatIf={WhatIf}, force={Force})", job.Key, whatIf, job.Options.Force);

        var sourceConnector = job.SourceConnector;
        var destinationConnector = job.DestinationConnector;

        var filter = job.Filter;

        await sourceConnector.AuthenticateAsync(cancellationToken);
        await destinationConnector.AuthenticateAsync(cancellationToken);

        var existingLinks = await linkStateRepo.GetByJobAsync(job.Key, cancellationToken);
        JobExecutionResult result = new() { JobKey = job.Key };
        string filterScope = filter?.Scope ?? string.Empty;

        // --- Read current snapshots from both sides ---
        var currentSourcePageSet = await ReadCurrentPagesAsync(sourceConnector, job.Options.Source, cancellationToken);
        var currentSourceItems = filter is not null ? filter.Apply(currentSourcePageSet.Items) : currentSourcePageSet.Items;

        var currentDestinationPageSet = await ReadCurrentPagesAsync(destinationConnector, job.Options.Destination, cancellationToken);
        var currentDestinationItems = filter is not null ? filter.Apply(currentDestinationPageSet.Items) : currentDestinationPageSet.Items;

        // --- Poll incremental changes (for cursor tracking) ---
        var sourceCursorState = await cursorRepo.GetCursorAsync(job.Key, job.Options.Source, cancellationToken);
        string? sourceCursor = GetApplicableCursor(job.Key, job.Options.Source, sourceCursorState, filterScope);
        var sourcePageSet = await ReadAllPagesAsync(sourceConnector, job.Options.Source, sourceCursor, job.Options.Full || job.Options.Force, cancellationToken);

        var destinationCursorState = await cursorRepo.GetCursorAsync(job.Key, job.Options.Destination, cancellationToken);
        string? destinationCursor = GetApplicableCursor(job.Key, job.Options.Destination, destinationCursorState, filterScope);
        var destinationPageSet = await ReadAllPagesAsync(destinationConnector, job.Options.Destination, destinationCursor, job.Options.Full || job.Options.Force, cancellationToken);

        // --- Plan actions (single pass over full current state from both sides) ---
        var actions = planner.PlanActions(job.Options, currentSourceItems, currentDestinationItems, existingLinks);

        var actionsSkip = actions.Where(a => a.Kind == Skip).ToList();
        var actionsToDestination = actions.Where(a => a.Direction == SourceToDestination).Except(actionsSkip).ToList();
        var actionsToSource = actions.Where(a => a.Direction == DestinationToSource).Except(actionsSkip).ToList();

        logger.LogInformation("Job {job.Key}: {CountDestination} actions targeting destination, {CountSource} targeting source, {CountSkip} skip", job.Key, actionsToDestination.Count, actionsToSource.Count, actionsSkip.Count);
        result.ActionsPlanned += actionsToSource.Count + actionsToDestination.Count;

        if (whatIf)
        {
            LogPlannedActions(job.Key, actions);
        }

        if (!whatIf)
        {
            await EnsureActionItemsLoadedAsync(actionsToDestination, cancellationToken);
            await ApplyActionsAsync(actionsToDestination, direction: SourceToDestination, job: job, existingLinks: existingLinks, cancellationToken: cancellationToken);
            existingLinks = await linkStateRepo.GetByJobAsync(job.Key, cancellationToken);

            await EnsureActionItemsLoadedAsync(actionsToSource, cancellationToken);
            await ApplyActionsAsync(actionsToSource, direction: DestinationToSource, job: job, existingLinks: existingLinks, cancellationToken: cancellationToken);

            var skipsWithMatches = actionsSkip.Where(a => a.Item is not null && a.MatchedTargetItem is not null).ToList();
            if (skipsWithMatches.Count > 0)
            {
                existingLinks = await linkStateRepo.GetByJobAsync(job.Key, cancellationToken);

                var destinationSkipsWithMatches = skipsWithMatches
                    .Where(a => a.Direction == SourceToDestination)
                    .ToList();
                if (destinationSkipsWithMatches.Count > 0)
                {
                    await RecordUnchangedLinksAsync(job.Key, job.Options.EntityType, destinationSkipsWithMatches, existingLinks, SourceToDestination, cancellationToken);
                }

                var sourceSkipsWithMatches = skipsWithMatches
                    .Where(a => a.Direction == DestinationToSource)
                    .ToList();
                if (sourceSkipsWithMatches.Count > 0)
                {
                    await RecordUnchangedLinksAsync(job.Key, job.Options.EntityType, sourceSkipsWithMatches, existingLinks, DestinationToSource, cancellationToken);
                }
            }

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

    private async Task ApplyActionsAsync<TItem>(
        IReadOnlyList<SyncAction<TItem>> actions,
        SyncDirection direction,
        Job<TItem> job,
        IReadOnlyList<LinkStateRow> existingLinks,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        string entityType = job.Options.EntityType;
        var targetConnector = direction == SourceToDestination ? job.DestinationConnector : job.SourceConnector;

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
                    case Create when action.Item is not null:
                        {
                            var created = await targetConnector.CreateItemAsync(action.Item, cancellationToken);
                            logger.LogInformation("Job {JobKey}: created {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);
                            await opLog.AppendAsync(job.Key, entityType, "create", created.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = new LinkStateRow
                            {
                                JobKey = job.Key,
                                EntityType = entityType,
                                SourceId = direction == SourceToDestination ? action.Item.Provenance.ProviderId : created.Provenance.ProviderId,
                                DestinationId = direction == SourceToDestination ? created.Provenance.ProviderId : action.Item.Provenance.ProviderId,
                                SourceVersion = direction == SourceToDestination ? action.Item.Provenance.Version : created.Provenance.Version,
                                DestinationVersion = direction == SourceToDestination ? created.Provenance.Version : action.Item.Provenance.Version,
                                SourceHash = direction == SourceToDestination ? action.Item.Provenance.ContentHash : created.Provenance.ContentHash,
                                DestinationHash = direction == SourceToDestination ? created.Provenance.ContentHash : action.Item.Provenance.ContentHash,
                                OriginSide = direction == SourceToDestination ? "Source" : "Destination",
                                LastSyncedAt = DateTimeOffset.UtcNow,
                                LastSyncResult = "created",
                            };
                            await linkStateRepo.UpsertAsync(link, cancellationToken);
                            break;
                        }

                    case Update when action.Item is not null:
                        {
                            var link = FindLinkForUpdate(existingLinks, direction, action.Item.Provenance.ProviderId);
                            if (link is null)
                            {
                                if (action.MatchedTargetItem is null)
                                {
                                    continue;
                                }

                                var matchedTarget = CreateTargetItem(action.Item, null, direction, action.MatchedTargetItem);

                                DetachPhotoIfUnchanged(action, matchedTarget);

                                var matchedUpdate = await targetConnector.UpdateItemAsync(matchedTarget, cancellationToken);
                                logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);
                                await opLog.AppendAsync(job.Key, entityType, "update", matchedUpdate.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                                var matchedLink = new LinkStateRow
                                {
                                    JobKey = job.Key,
                                    EntityType = entityType,
                                    SourceId = direction == SourceToDestination ? action.Item.Provenance.ProviderId : matchedUpdate.Provenance.ProviderId,
                                    DestinationId = direction == SourceToDestination ? matchedUpdate.Provenance.ProviderId : action.Item.Provenance.ProviderId,
                                    SourceVersion = direction == SourceToDestination ? action.Item.Provenance.Version : matchedUpdate.Provenance.Version,
                                    DestinationVersion = direction == SourceToDestination ? matchedUpdate.Provenance.Version : action.Item.Provenance.Version,
                                    SourceHash = direction == SourceToDestination ? action.Item.Provenance.ContentHash : matchedUpdate.Provenance.ContentHash,
                                    DestinationHash = direction == SourceToDestination ? matchedUpdate.Provenance.ContentHash : action.Item.Provenance.ContentHash,
                                    OriginSide = direction == SourceToDestination ? "Source" : "Destination",
                                    LastSyncedAt = DateTimeOffset.UtcNow,
                                    LastSyncResult = "updated",
                                };
                                await linkStateRepo.UpsertAsync(matchedLink, cancellationToken);
                                break;
                            }

                            var targetItem = CreateTargetItem(action.Item, link, direction, action.MatchedTargetItem);

                            DetachPhotoIfUnchanged(action, targetItem);

                            var updated = await targetConnector.UpdateItemAsync(targetItem, cancellationToken);
                            logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);
                            await opLog.AppendAsync(job.Key, entityType, "update", updated.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            if (direction == SourceToDestination)
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
                            await linkStateRepo.UpsertAsync(link, cancellationToken);

                            break;
                        }

                    case Delete when action.DeleteId is not null:
                        {
                            await targetConnector.DeleteItemAsync(action.DeleteId, cancellationToken);
                            logger.LogInformation("Job {JobKey}: deleted item {ItemId} on side {Side}", job.Key, action.DeleteId, direction);
                            await opLog.AppendAsync(job.Key, entityType, "delete", action.DeleteId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = direction == SourceToDestination
                                ? linksByDestinationId.GetValueOrDefault(action.DeleteId)
                                : linksBySourceId.GetValueOrDefault(action.DeleteId);

                            if (link is not null)
                            {
                                if (direction == SourceToDestination)
                                {
                                    link.DestinationDeleted = true;
                                }
                                else
                                {
                                    link.SourceDeleted = true;
                                }

                                link.LastSyncedAt = DateTimeOffset.UtcNow;
                                link.LastSyncResult = "deleted";
                                await linkStateRepo.UpsertAsync(link, cancellationToken);
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
                    Create => "create",
                    Update => "update",
                    Delete => "delete",
                    _ => action.Kind.ToString().ToLowerInvariant()
                };

                logger.LogError(ex, "Job {JobKey}: failed to apply {Kind} action for item {ItemId} on side {Side}; skipping",
                    job.Key, action.Kind, itemId, direction);
                await opLog.AppendAsync(job.Key, entityType, operation, itemId, direction.ToString(), "error", cancellationToken: cancellationToken);
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
                "What-if job {JobKey}: would {Verb} {Description} in direction {TargetSide} ({Reason})",
                jobKey,
                verb,
                DescribeActionTarget(action),
                action.Direction,
                action.Reason ?? "no reason provided");
        }
    }

    private static string DescribeActionTarget<TItem>(SyncAction<TItem> action) where TItem : CanonicalItem
    {
        if (action.Item is CanonicalContact contact)
        {
            string name = ContactNameHelper.GetName(contact);
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
        SyncDirection direction,
        string providerId)
    {
        return direction == SourceToDestination
            ? existingLinks.FirstOrDefault(link => link.SourceId == providerId)
            : existingLinks.FirstOrDefault(link => link.DestinationId == providerId);
    }

    private static async Task EnsureActionItemsLoadedAsync<TItem>(IReadOnlyList<SyncAction<TItem>> actions, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        foreach (var action in actions)
        {
            if (action.Kind is Create or Update && action.Item is CanonicalContact contact)
            {
                await ContactPhotoLoader.EnsureLoadedAsync(contact, cancellationToken);
            }

            if (action.Kind == Update && action.MatchedTargetItem is CanonicalContact targetContact)
            {
                await ContactPhotoLoader.EnsureLoadedAsync(targetContact, cancellationToken);
            }
        }
    }

    private static TItem CreateTargetItem<TItem>(
        TItem sourceItem,
        LinkStateRow? link,
        SyncDirection updateDirection,
        CanonicalItem? matchedTargetItem) where TItem : CanonicalItem
    {
        var targetItem = sourceItem with
        {
            Provenance = new()
            {
                ProviderId = matchedTargetItem?.Provenance.ProviderId ??
                             (updateDirection == SourceToDestination ? link!.DestinationId! : link!.SourceId),
                Version = matchedTargetItem?.Provenance.Version ??
                          (updateDirection == SourceToDestination ? link!.DestinationVersion : link!.SourceVersion),
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

        if (ContactPhotoMetadataHelper.PhotoHashesMatch(action.Item as CanonicalContact, action.MatchedTargetItem as CanonicalContact))
        {
            ContactPhotoMetadataHelper.DetachPhoto((matchedTarget as CanonicalContact)!);
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
        SyncDirection direction,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        foreach (var action in unchangedActions)
        {
            if (action.Item is null || action.MatchedTargetItem is null)
            {
                continue;
            }

            var link = FindLinkForUpdate(existingLinks, direction, action.Item.Provenance.ProviderId)
                ?? new LinkStateRow
                {
                    JobKey = jobKey,
                    EntityType = entityType,
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

    private sealed record PageSet<TItem>(IReadOnlyList<TItem> Items, string? Cursor) where TItem : CanonicalItem;
}
