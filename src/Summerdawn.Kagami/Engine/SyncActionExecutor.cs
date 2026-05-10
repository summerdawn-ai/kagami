using System.Net;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Applies planned sync actions for a single sync pass.
/// </summary>
/// <remarks>
/// Owns all execution-side behaviour: what-if logging, ensuring action items are loaded,
/// performing create / update / delete writes against the target connector, updating
/// link-state rows, writing operation-log entries, and fault handling.
/// <para>
/// Fault policy:
/// <list type="bullet">
///   <item>Transient provider failures (HTTP 429 or 5xx) throw
///   <see cref="OperationFaultedException"/> immediately, aborting the entire pass
///   without processing further items.</item>
///   <item>Permanent per-item failures (all other exceptions) are logged per item and
///   remaining items continue to be processed.  After the pass completes, a warning is
///   logged summarising the failures; cursors are still advanced so that the bad item
///   is not retried on every subsequent run.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SyncActionExecutor(
    LinkStateRepository linkStateRepo,
    OperationLogRepository opLog,
    ILogger<SyncActionExecutor> logger)
{
    /// <summary>
    /// Logs all planned actions for a what-if run without performing any writes.
    /// </summary>
    public void LogPlannedActions<TItem>(string jobKey, IReadOnlyList<SyncAction<TItem>> actions) where TItem : CanonicalItem
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

    /// <summary>
    /// Loads all action items and then applies every planned action against the target connector.
    /// </summary>
    /// <remarks>
    /// A transient provider failure throws <see cref="OperationFaultedException"/> immediately.
    /// Permanent per-item failures are logged per item; remaining items continue to be
    /// processed and a summary warning is emitted after the pass.  Cursors are advanced
    /// regardless of permanent per-item failures.
    /// </remarks>
    /// <exception cref="OperationFaultedException">
    /// Thrown only when a transient failure aborts the pass.
    /// </exception>
    public async Task ApplyActionsAsync<TItem>(
        IReadOnlyList<SyncAction<TItem>> actions,
        SyncDirection direction,
        Job<TItem> job,
        IReadOnlyList<LinkStateRow> existingLinks,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        await EnsureActionItemsLoadedAsync(actions, cancellationToken);

        string entityType = job.Options.EntityType;
        var targetConnector = direction == SourceToDestination ? job.DestinationConnector : job.SourceConnector;

        var linksBySourceId = existingLinks.ToDictionary(l => l.SourceId);
        var linksByDestinationId = existingLinks
            .Where(l => l.DestinationId != null)
            .ToDictionary(l => l.DestinationId!);

        bool failedActions = false;

        foreach (var action in actions)
        {
            bool succeeded = await ApplyActionAsync(
                action,
                direction,
                job,
                entityType,
                targetConnector,
                existingLinks,
                linksBySourceId,
                linksByDestinationId,
                cancellationToken);

            if (!succeeded)
            {
                failedActions = true;
            }
        }

        if (failedActions)
        {
            logger.LogWarning(
                "Job {JobKey}: one or more synchronization actions failed. Check the log messages above for details.",
                job.Key);
        }
    }

    /// <summary>
    /// Applies a single planned sync action and returns <c>true</c> on success or <c>false</c>
    /// on a permanent per-item failure.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the action succeeded; <c>false</c> when a permanent per-item failure
    /// occurred and processing should continue for remaining items.
    /// </returns>
    /// <exception cref="OperationFaultedException">
    /// Thrown when a transient failure is encountered, aborting the entire pass.
    /// </exception>
    private async Task<bool> ApplyActionAsync<TItem>(
        SyncAction<TItem> action,
        SyncDirection direction,
        Job<TItem> job,
        string entityType,
        IConnector<TItem> targetConnector,
        IReadOnlyList<LinkStateRow> existingLinks,
        Dictionary<string, LinkStateRow> linksBySourceId,
        Dictionary<string, LinkStateRow> linksByDestinationId,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        try
        {
            switch (action.Kind)
            {
                case Create when action.Item is not null:
                    {
                        var created = await targetConnector.CreateItemAsync(action.Item, cancellationToken);
                        logger.LogInformation("Job {JobKey}: created {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);

                        if (!job.Options.NoPersistence)
                        {
                            await opLog.AppendAsync(job.Key, entityType, "create", created.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = new LinkStateRow
                            {
                                PartitionKey = job.PartitionKey,
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
                        }
                        break;
                    }

                case Update when action.Item is not null:
                    {
                        var link = FindLinkForUpdate(existingLinks, direction, action.Item.Provenance.ProviderId);
                        if (link is null)
                        {
                            if (action.MatchedTargetItem is null)
                            {
                                return true;
                            }

                            var matchedTarget = CreateTargetItem(action.Item, null, direction, action.MatchedTargetItem);

                            DetachPhotoIfUnchanged(action, matchedTarget);

                            var matchedUpdate = await targetConnector.UpdateItemAsync(matchedTarget, cancellationToken);
                            logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);

                            if (!job.Options.NoPersistence)
                            {
                                await opLog.AppendAsync(job.Key, entityType, "update", matchedUpdate.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                                var matchedLink = new LinkStateRow
                                {
                                    PartitionKey = job.PartitionKey,
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
                            }
                            break;
                        }

                        var targetItem = CreateTargetItem(action.Item, link, direction, action.MatchedTargetItem);

                        DetachPhotoIfUnchanged(action, targetItem);

                        var updated = await targetConnector.UpdateItemAsync(targetItem, cancellationToken);
                        logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);

                        if (!job.Options.NoPersistence)
                        {
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
                        }

                        break;
                    }

                case Delete when action.DeleteId is not null:
                    {
                        await targetConnector.DeleteItemAsync(action.DeleteId, cancellationToken);
                        logger.LogInformation("Job {JobKey}: deleted item {ItemId} on side {Side}", job.Key, action.DeleteId, direction);

                        if (!job.Options.NoPersistence)
                        {
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
                        }

                        break;
                    }
            }

            return true;
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

            return await HandleActionExceptionAsync(ex, job.Key, entityType, operation, itemId, direction, job.Options.NoPersistence, cancellationToken);
        }
    }

    /// <summary>
    /// Classifies an action-level exception and either throws
    /// <see cref="OperationFaultedException"/> for transient failures or returns <c>false</c>
    /// for permanent per-item failures so that remaining items can continue.
    /// </summary>
    /// <returns>
    /// Always <c>false</c> for permanent per-item failures; never returns for transient
    /// failures (throws instead).
    /// </returns>
    private async Task<bool> HandleActionExceptionAsync(
        Exception ex,
        string jobKey,
        string entityType,
        string operation,
        string itemId,
        SyncDirection direction,
        bool noPersistence,
        CancellationToken cancellationToken)
    {
        if (!noPersistence)
        {
            await opLog.AppendAsync(jobKey, entityType, operation, itemId, direction.ToString(), "error", cancellationToken: cancellationToken);
        }

        if (IsTransientFailure(ex))
        {
            logger.LogError(
                ex,
                "Job {JobKey}: transient failure applying {Operation} for item {ItemId} on side {Side}; aborting run",
                jobKey, operation, itemId, direction);

            throw new OperationFaultedException(
                $"Transient failure applying {operation} for item '{itemId}' on side {direction}.",
                ex);
        }

        logger.LogError(
            ex,
            "Job {JobKey}: failed to apply {Operation} for item {ItemId} on side {Side}; continuing",
            jobKey, operation, itemId, direction);

        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="ex"/> represents a transient provider failure
    /// (HTTP 429 or any 5xx) that should abort the current run.
    /// </summary>
    private static bool IsTransientFailure(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            int? code = (int?)httpEx.StatusCode;
            return code == (int)HttpStatusCode.TooManyRequests || (code >= 500 && code < 600);
        }

        return false;
    }

    /// <summary>
    /// Triggers lazy photo loading for all contacts involved in create or update actions.
    /// Photos are loaded before the write pass so that connectors receive the full payload.
    /// </summary>
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

    /// <summary>
    /// Returns a human-readable description of the target item in <paramref name="action"/>.
    /// </summary>
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

    /// <summary>
    /// Finds the persisted link row for an update action, or returns <c>null</c> when no link
    /// exists yet.
    /// </summary>
    private static LinkStateRow? FindLinkForUpdate(
        IReadOnlyList<LinkStateRow> existingLinks,
        SyncDirection direction,
        string providerId)
    {
        return direction == SourceToDestination
            ? existingLinks.FirstOrDefault(link => link.SourceId == providerId)
            : existingLinks.FirstOrDefault(link => link.DestinationId == providerId);
    }

    /// <summary>
    /// Builds the target item for an update by copying <paramref name="sourceItem"/> and
    /// overwriting the provenance with the target-side provider ID and version.
    /// </summary>
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

    /// <summary>
    /// Removes photo metadata from <paramref name="matchedTarget"/> when the photo hashes of
    /// the action's source and matched target items are equal, avoiding a redundant upload.
    /// </summary>
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
}
