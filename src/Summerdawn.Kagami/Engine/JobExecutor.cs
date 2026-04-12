namespace Summerdawn.Kagami.Engine;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

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
    /// <param name="connectorA">Connector for side A.</param>
    /// <param name="connectorB">Connector for side B.</param>
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
        IConnector connectorA,
        IConnector connectorB,
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
            return await RunJobAsync(jobKey, jobOptions, connectorA, connectorB, whatIf, filter, force, cancellationToken);
        }
        finally
        {
            await leaseRepo.ReleaseAsync(jobKey, holderId, cancellationToken);
        }
    }

    private async Task<JobExecutionResult> RunJobAsync(
        string jobKey,
        JobOptions jobOptions,
        IConnector connectorA,
        IConnector connectorB,
        bool whatIf,
        ContactFilter? filter,
        bool force,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting job {JobKey} (whatIf={WhatIf}, force={Force})", jobKey, whatIf, force);

        await connectorA.AuthenticateAsync(cancellationToken);
        await connectorB.AuthenticateAsync(cancellationToken);

        IReadOnlyList<LinkStateRow> existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);
        JobExecutionResult result = new() { JobKey = jobKey };
        string filterScope = filter?.Scope ?? string.Empty;

        // --- Poll side A ---
        EndpointCursorState? cursorStateA = await cursorRepo.GetCursorAsync(jobKey, jobOptions.EndpointA, cancellationToken);
        string? cursorA = GetApplicableCursor(jobKey, jobOptions.EndpointA, cursorStateA, filterScope);
        var pageSetA = await ReadAllPagesAsync(connectorA, jobOptions.EndpointA, cursorA, force, cancellationToken);
        IReadOnlyList<CanonicalItem> itemsA = filter is not null ? filter.Apply(pageSetA.Items) : pageSetA.Items;

        // --- Read current state for side A/B ---
        var currentPageSetA = await ReadCurrentPagesAsync(connectorA, jobOptions.EndpointA, cancellationToken);
        IReadOnlyList<CanonicalItem> currentItemsA = filter is not null ? filter.Apply(currentPageSetA.Items) : currentPageSetA.Items;

        var currentPageSetB = await ReadCurrentPagesAsync(connectorB, jobOptions.EndpointB, cancellationToken);
        IReadOnlyList<CanonicalItem> currentItemsB = filter is not null ? filter.Apply(currentPageSetB.Items) : currentPageSetB.Items;

        // --- Poll side B ---
        EndpointCursorState? cursorStateB = await cursorRepo.GetCursorAsync(jobKey, jobOptions.EndpointB, cancellationToken);
        string? cursorB = GetApplicableCursor(jobKey, jobOptions.EndpointB, cursorStateB, filterScope);
        var pageSetB = await ReadAllPagesAsync(connectorB, jobOptions.EndpointB, cursorB, force, cancellationToken);
        IReadOnlyList<CanonicalItem> itemsB = filter is not null ? filter.Apply(pageSetB.Items) : pageSetB.Items;

        // --- Plan actions from A to B ---
        var actionsAtoB = planner.PlanFromSideA(jobOptions, itemsA, currentItemsB, existingLinks, force);
        logger.LogInformation("Job {JobKey}: {Count} actions planned from A to B", jobKey, actionsAtoB.Count);
        result.ActionsPlanned += actionsAtoB.Count;

        // --- Plan actions from B to A ---
        var actionsBtoA = planner.PlanFromSideB(jobOptions, itemsB, currentItemsA, existingLinks, force);
        logger.LogInformation("Job {JobKey}: {Count} actions planned from B to A", jobKey, actionsBtoA.Count);
        result.ActionsPlanned += actionsBtoA.Count;

        if (whatIf)
        {
            LogPlannedActions(jobKey, actionsAtoB);
            LogPlannedActions(jobKey, actionsBtoA);
        }

        if (!whatIf)
        {
            await EnsureActionItemsLoadedAsync(actionsAtoB, cancellationToken);
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsAtoB, connectorB, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.B, cancellationToken);
            existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);

            await EnsureActionItemsLoadedAsync(actionsBtoA, cancellationToken);
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsBtoA, connectorA, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.A, cancellationToken);

            if (pageSetA.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(jobKey, jobOptions.EndpointA, filterScope, pageSetA.Cursor, cancellationToken);
            }

            if (pageSetB.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(jobKey, jobOptions.EndpointB, filterScope, pageSetB.Cursor, cancellationToken);
            }
        }

        result.Succeeded = true;
        logger.LogInformation("Job {JobKey} completed (whatIf={WhatIf}, actionsPlanned={Count})", jobKey, whatIf, result.ActionsPlanned);
        return result;
    }

    private static async Task ApplyActionsAsync(
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
        var linksBySideAId = existingLinks.ToDictionary(l => l.SideAId);
        var linksBySideBId = existingLinks
            .Where(l => l.SideBId != null)
            .ToDictionary(l => l.SideBId!);

        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case SyncActionKind.Create when action.Item is not null:
                    {
                        var created = await targetConnector.CreateItemAsync(action.Item, cancellationToken);
                        await operationLog.AppendAsync(jobKey, entityType, "create", created.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                        var link = new LinkStateRow
                        {
                            JobKey = jobKey,
                            EntityType = entityType,
                            SideAId = updateSide == SyncSide.B ? action.Item.SourceId : created.SourceId,
                            SideBId = updateSide == SyncSide.B ? created.SourceId : action.Item.SourceId,
                            SideAVersion = updateSide == SyncSide.B ? action.Item.Version : created.Version,
                            SideBVersion = updateSide == SyncSide.B ? created.Version : action.Item.Version,
                            SideAHash = updateSide == SyncSide.B ? action.Item.ContentHash : created.ContentHash,
                            SideBHash = updateSide == SyncSide.B ? created.ContentHash : action.Item.ContentHash,
                            OriginSide = updateSide == SyncSide.B ? "A" : "B",
                            LastSyncedAt = DateTimeOffset.UtcNow,
                            LastSyncResult = "created",
                        };
                        await linkStateRepository.UpsertAsync(link, cancellationToken);
                        break;
                    }

                case SyncActionKind.Update when action.Item is not null:
                    {
                        LinkStateRow? link = FindLinkForUpdate(existingLinks, updateSide, action.Item.SourceId);
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

                            CanonicalItem matchedTarget = CreateTargetItem(action.Item, null, updateSide, action.MatchedTargetItem);
                            var matchedUpdate = await targetConnector.UpdateItemAsync(matchedTarget, cancellationToken);
                            await operationLog.AppendAsync(jobKey, entityType, "update", matchedUpdate.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                            var matchedLink = new LinkStateRow
                            {
                                JobKey = jobKey,
                                EntityType = entityType,
                                SideAId = updateSide == SyncSide.B ? action.Item.SourceId : matchedUpdate.SourceId,
                                SideBId = updateSide == SyncSide.B ? matchedUpdate.SourceId : action.Item.SourceId,
                                SideAVersion = updateSide == SyncSide.B ? action.Item.Version : matchedUpdate.Version,
                                SideBVersion = updateSide == SyncSide.B ? matchedUpdate.Version : action.Item.Version,
                                SideAHash = updateSide == SyncSide.B ? action.Item.ContentHash : matchedUpdate.ContentHash,
                                SideBHash = updateSide == SyncSide.B ? matchedUpdate.ContentHash : action.Item.ContentHash,
                                OriginSide = updateSide == SyncSide.B ? "A" : "B",
                                LastSyncedAt = DateTimeOffset.UtcNow,
                                LastSyncResult = "updated",
                            };
                            await linkStateRepository.UpsertAsync(matchedLink, cancellationToken);
                            break;
                        }

                        CanonicalItem targetItem = CreateTargetItem(action.Item, link, updateSide, action.MatchedTargetItem);
                        var updated = await targetConnector.UpdateItemAsync(targetItem, cancellationToken);
                        await operationLog.AppendAsync(jobKey, entityType, "update", updated.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                        if (updateSide == SyncSide.B)
                        {
                            link.SideAVersion = action.Item.Version;
                            link.SideAHash = action.Item.ContentHash;
                            link.SideBVersion = updated.Version;
                            link.SideBHash = updated.ContentHash;
                        }
                        else
                        {
                            link.SideBVersion = action.Item.Version;
                            link.SideBHash = action.Item.ContentHash;
                            link.SideAVersion = updated.Version;
                            link.SideAHash = updated.ContentHash;
                        }

                        link.LastSyncedAt = DateTimeOffset.UtcNow;
                        link.LastSyncResult = "updated";
                        await linkStateRepository.UpsertAsync(link, cancellationToken);

                        break;
                    }

                case SyncActionKind.Delete when action.DeleteId is not null:
                    {
                        await targetConnector.DeleteItemAsync(action.DeleteId, cancellationToken);
                        await operationLog.AppendAsync(jobKey, entityType, "delete", action.DeleteId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                        var link = updateSide == SyncSide.B
                            ? linksBySideBId.GetValueOrDefault(action.DeleteId)
                            : linksBySideAId.GetValueOrDefault(action.DeleteId);

                        if (link is not null)
                        {
                            if (updateSide == SyncSide.B)
                            {
                                link.SideBDeleted = true;
                            }
                            else
                            {
                                link.SideADeleted = true;
                            }

                            link.LastSyncedAt = DateTimeOffset.UtcNow;
                            link.LastSyncResult = "deleted";
                            await linkStateRepository.UpsertAsync(link, cancellationToken);
                        }

                        break;
                    }
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
        foreach (SyncAction action in actions.Where(a => a.Kind is SyncActionKind.Create or SyncActionKind.Update or SyncActionKind.Delete))
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
        return updateSide == SyncSide.B
            ? existingLinks.FirstOrDefault(link => link.SideAId == sourceId)
            : existingLinks.FirstOrDefault(link => link.SideBId == sourceId);
    }

    private static async Task EnsureActionItemsLoadedAsync(IReadOnlyList<SyncAction> actions, CancellationToken cancellationToken)
    {
        foreach (SyncAction action in actions)
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
            SourceId = matchedTargetItem?.SourceId ?? (updateSide == SyncSide.B ? link!.SideBId! : link!.SideAId),
            Version = matchedTargetItem?.Version ?? (updateSide == SyncSide.B ? link!.SideBVersion : link!.SideAVersion),
            ContentHash = sourceItem.ContentHash,
            IsDeleted = sourceItem.IsDeleted,
            Metadata = new Dictionary<string, string>(sourceItem.Metadata),
        };

    private static bool IsDuplicateLinkAction(LinkStateRow link, SyncAction action, SyncSide updateSide) =>
        action.Item is not null
        && action.MatchedTargetItem is not null
        && (updateSide == SyncSide.B
            ? link.SideAId == action.Item.SourceId && link.SideBId == action.MatchedTargetItem.SourceId
            : link.SideBId == action.Item.SourceId && link.SideAId == action.MatchedTargetItem.SourceId);

    private sealed record PageSet(IReadOnlyList<CanonicalItem> Items, string? Cursor);
}
