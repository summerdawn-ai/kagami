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
///   <item>Permanent per-item failures (all other exceptions) are logged; remaining items
///   continue to be processed.  After the pass completes, if any item failed,
///   <see cref="OperationFaultedException"/> is thrown so that cursors do not advance.</item>
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
    /// After the pass, if any permanent per-item failure occurred,
    /// <see cref="OperationFaultedException"/> is thrown so that the caller does not advance
    /// cursors.
    /// </remarks>
    /// <exception cref="OperationFaultedException">
    /// Thrown when a transient failure aborts the pass, or when the pass completes but at
    /// least one item failed permanently.
    /// </exception>
    public async Task ApplyActionsAsync<TItem>(
        IReadOnlyList<SyncAction<TItem>> actions,
        SyncDirection direction,
        Job<TItem> job,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        await EnsureActionItemsLoadedAsync(actions, cancellationToken);

        string entityType = job.Options.EntityType;
        var targetConnector = direction == SourceToDestination ? job.DestinationConnector : job.SourceConnector;

        bool failedActions = false;

        foreach (var action in actions)
        {
            bool succeeded = await ApplyActionAsync(
                action,
                direction,
                job,
                entityType,
                targetConnector,
                cancellationToken);

            if (!succeeded)
            {
                failedActions = true;
            }
        }

        if (failedActions)
        {
            throw new OperationFaultedException(
                $"One or more sync actions failed for job '{job.Key}'; cursors will not advance.");
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
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        var originItem = action.GetOriginItem();
        var matchedTargetItem = action.GetTargetItem();

        try
        {
            switch (action.Kind)
            {
                case Create when originItem is not null:
                    {
                        var createdItem = await targetConnector.CreateItemAsync(originItem, cancellationToken);
                        logger.LogInformation("Job {JobKey}: created {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);

                        if (!job.Options.NoPersistence)
                        {
                            await opLog.AppendAsync(job.Key, entityType, "create", createdItem.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = new LinkStateRow
                            {
                                PartitionKey = job.PartitionKey,
                                SourceId = direction == SourceToDestination ? originItem.Provenance.ProviderId : createdItem.Provenance.ProviderId,
                                DestinationId = direction == SourceToDestination ? createdItem.Provenance.ProviderId : originItem.Provenance.ProviderId,
                                SourceVersion = direction == SourceToDestination ? originItem.Provenance.Version : createdItem.Provenance.Version,
                                DestinationVersion = direction == SourceToDestination ? createdItem.Provenance.Version : originItem.Provenance.Version,
                                SourceHash = direction == SourceToDestination ? originItem.Provenance.ContentHash : createdItem.Provenance.ContentHash,
                                DestinationHash = direction == SourceToDestination ? createdItem.Provenance.ContentHash : originItem.Provenance.ContentHash,
                                OriginSide = direction == SourceToDestination ? "Source" : "Destination",
                                LastSyncedAt = DateTimeOffset.UtcNow,
                                LastSyncResult = "created",
                            };
                            await linkStateRepo.UpsertAsync(link, cancellationToken);
                        }
                        break;
                    }

                case Update when originItem is not null:
                    {
                        var link = action.Link.PersistedState;
                        if (link is null)
                        {
                            if (matchedTargetItem is null)
                            {
                                return true;
                            }

                            var targetItemToWrite = CreateTargetItemToWrite(originItem, null, direction, matchedTargetItem);

                            DetachPhotoIfUnchanged(action, targetItemToWrite);

                            var updatedTargetItem = await targetConnector.UpdateItemAsync(targetItemToWrite, cancellationToken);
                            logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);

                            if (!job.Options.NoPersistence)
                            {
                                await opLog.AppendAsync(job.Key, entityType, "update", updatedTargetItem.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                                var matchedLink = new LinkStateRow
                                {
                                    PartitionKey = job.PartitionKey,
                                    SourceId = direction == SourceToDestination ? originItem.Provenance.ProviderId : updatedTargetItem.Provenance.ProviderId,
                                    DestinationId = direction == SourceToDestination ? updatedTargetItem.Provenance.ProviderId : originItem.Provenance.ProviderId,
                                    SourceVersion = direction == SourceToDestination ? originItem.Provenance.Version : updatedTargetItem.Provenance.Version,
                                    DestinationVersion = direction == SourceToDestination ? updatedTargetItem.Provenance.Version : originItem.Provenance.Version,
                                    SourceHash = direction == SourceToDestination ? originItem.Provenance.ContentHash : updatedTargetItem.Provenance.ContentHash,
                                    DestinationHash = direction == SourceToDestination ? updatedTargetItem.Provenance.ContentHash : originItem.Provenance.ContentHash,
                                    OriginSide = direction == SourceToDestination ? "Source" : "Destination",
                                    LastSyncedAt = DateTimeOffset.UtcNow,
                                    LastSyncResult = "updated",
                                };
                                await linkStateRepo.UpsertAsync(matchedLink, cancellationToken);
                            }
                        }
                        else
                        {
                            var targetItemToWrite = CreateTargetItemToWrite(originItem, link, direction, matchedTargetItem);

                            DetachPhotoIfUnchanged(action, targetItemToWrite);

                            var updatedTargetItem = await targetConnector.UpdateItemAsync(targetItemToWrite, cancellationToken);
                            logger.LogInformation("Job {JobKey}: updated {Description} on side {Side}", job.Key, DescribeActionTarget(action), direction);

                            if (!job.Options.NoPersistence)
                            {
                                await opLog.AppendAsync(job.Key, entityType, "update", updatedTargetItem.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                                if (direction == SourceToDestination)
                                {
                                    link.SourceVersion = originItem.Provenance.Version;
                                    link.SourceHash = originItem.Provenance.ContentHash;
                                    link.DestinationVersion = updatedTargetItem.Provenance.Version;
                                    link.DestinationHash = updatedTargetItem.Provenance.ContentHash;
                                }
                                else
                                {
                                    link.DestinationVersion = originItem.Provenance.Version;
                                    link.DestinationHash = originItem.Provenance.ContentHash;
                                    link.SourceVersion = updatedTargetItem.Provenance.Version;
                                    link.SourceHash = updatedTargetItem.Provenance.ContentHash;
                                }

                                link.LastSyncedAt = DateTimeOffset.UtcNow;
                                link.LastSyncResult = "updated";
                                await linkStateRepo.UpsertAsync(link, cancellationToken);
                            }
                        }

                        break;
                    }

                case Delete when matchedTargetItem is not null:
                    {
                        string deleteId = matchedTargetItem.Provenance.ProviderId;

                        await targetConnector.DeleteItemAsync(deleteId, cancellationToken);
                        logger.LogInformation("Job {JobKey}: deleted item {ItemId} on side {Side}", job.Key, deleteId, direction);

                        if (!job.Options.NoPersistence)
                        {
                            await opLog.AppendAsync(job.Key, entityType, "delete", deleteId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            var link = action.Link.PersistedState;

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
            string itemId = originItem?.Provenance.ProviderId ?? matchedTargetItem?.Provenance.ProviderId ?? "unknown";
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
            if (action.Kind is Create or Update && action.GetOriginItem() is CanonicalContact contact)
            {
                await ContactPhotoLoader.EnsureLoadedAsync(contact, cancellationToken);
            }

            if (action.Kind == Update && action.GetTargetItem() is CanonicalContact targetContact)
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
        var itemToWrite = action.GetOriginItem();
        if (itemToWrite is CanonicalContact contact)
        {
            string name = ContactNameHelper.GetName(contact);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = itemToWrite.Provenance.ProviderId;
            }

            return $"contact '{name}'";
        }

        if (itemToWrite is not null)
        {
            return $"item '{itemToWrite.Provenance.ProviderId}'";
        }

        return $"item '{action.GetTargetItem()!.Provenance.ProviderId}'";
    }

    /// <summary>
    /// Builds the target item for an update by copying <paramref name="sourceItem"/> and
    /// overwriting the provenance with the target-side provider ID and version.
    /// </summary>
    private static TItem CreateTargetItemToWrite<TItem>(
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

        if (ContactPhotoMetadataHelper.PhotoHashesMatch(action.GetOriginItem() as CanonicalContact, action.GetTargetItem() as CanonicalContact))
        {
            ContactPhotoMetadataHelper.DetachPhoto((matchedTarget as CanonicalContact)!);
        }
    }
}
