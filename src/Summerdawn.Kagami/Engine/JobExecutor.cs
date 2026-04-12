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
    public async Task<JobExecutionResult> ExecuteAsync(
        string jobKey,
        JobOptions jobOptions,
        IConnector connectorA,
        IConnector connectorB,
        bool whatIf = false,
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
            return await RunJobAsync(jobKey, jobOptions, connectorA, connectorB, whatIf, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting job {JobKey} (whatIf={WhatIf})", jobKey, whatIf);

        await connectorA.AuthenticateAsync(cancellationToken);
        await connectorB.AuthenticateAsync(cancellationToken);

        IReadOnlyList<LinkStateRow> existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);
        JobExecutionResult result = new() { JobKey = jobKey };

        // --- Poll side A ---
        string? cursorA = await cursorRepo.GetCursorAsync(jobOptions.EndpointA, cancellationToken);
        var pageSetA = await ReadAllPagesAsync(connectorA, jobOptions.EndpointA, cursorA, cancellationToken);

        // --- Plan actions from A to B ---
        var actionsAtoB = planner.PlanFromSideA(jobOptions, pageSetA.Items, existingLinks);
        logger.LogInformation("Job {JobKey}: {Count} actions planned from A to B", jobKey, actionsAtoB.Count);
        result.ActionsPlanned += actionsAtoB.Count;

        if (whatIf)
        {
            LogPlannedActions(jobKey, actionsAtoB);
        }

        if (!whatIf)
        {
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsAtoB, connectorB, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.B, cancellationToken);
            existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);

            if (pageSetA.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(jobOptions.EndpointA, pageSetA.Cursor, cancellationToken);
            }
        }

        // --- Poll side B ---
        string? cursorB = await cursorRepo.GetCursorAsync(jobOptions.EndpointB, cancellationToken);
        var pageSetB = await ReadAllPagesAsync(connectorB, jobOptions.EndpointB, cursorB, cancellationToken);

        // --- Plan actions from B to A ---
        var actionsBtoA = planner.PlanFromSideB(jobOptions, pageSetB.Items, existingLinks);
        logger.LogInformation("Job {JobKey}: {Count} actions planned from B to A", jobKey, actionsBtoA.Count);
        result.ActionsPlanned += actionsBtoA.Count;

        if (whatIf)
        {
            LogPlannedActions(jobKey, actionsBtoA);
        }

        if (!whatIf)
        {
            await ApplyActionsAsync(jobKey, jobOptions.EntityType, actionsBtoA, connectorA, linkStateRepo, opLog, existingLinks, updateSide: SyncSide.A, cancellationToken);
            existingLinks = await linkStateRepo.GetByJobAsync(jobKey, cancellationToken);

            if (pageSetB.Cursor is not null)
            {
                await cursorRepo.SetCursorAsync(jobOptions.EndpointB, pageSetB.Cursor, cancellationToken);
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
                    if (link is null)
                    {
                        continue;
                    }

                    CanonicalItem targetItem = CreateTargetItem(action.Item, link, updateSide);
                    var updated = await targetConnector.UpdateItemAsync(targetItem, cancellationToken);
                    await operationLog.AppendAsync(jobKey, entityType, "update", updated.SourceId, updateSide.ToString(), "ok", cancellationToken: cancellationToken);

                    if (updateSide == SyncSide.B)
                    {
                        link.SideBVersion = updated.Version;
                        link.SideBHash = updated.ContentHash;
                    }
                    else
                    {
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
        CancellationToken cancellationToken)
    {
        IncrementalPage page;
        if (cursor is null)
        {
            logger.LogInformation("No cursor for {Endpoint}, performing initial full sync", endpointName);
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
            string name = string.IsNullOrWhiteSpace(contact.DisplayName)
                ? action.Item.SourceId
                : contact.DisplayName;
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

    private static CanonicalItem CreateTargetItem(CanonicalItem sourceItem, LinkStateRow link, SyncSide updateSide) =>
        new()
        {
            EntityType = sourceItem.EntityType,
            Payload = sourceItem.Payload,
            SourceId = updateSide == SyncSide.B ? link.SideBId! : link.SideAId,
            Version = updateSide == SyncSide.B ? link.SideBVersion : link.SideAVersion,
            ContentHash = sourceItem.ContentHash,
            IsDeleted = sourceItem.IsDeleted,
            Metadata = new Dictionary<string, string>(sourceItem.Metadata),
        };

    private sealed record PageSet(IReadOnlyList<CanonicalItem> Items, string? Cursor);
}
