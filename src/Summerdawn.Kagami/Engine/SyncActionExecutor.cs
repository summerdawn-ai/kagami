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
    ILogger<SyncActionExecutor> logger,
    Func<ConsoleKeyInfo>? readKey = null,
    Action<string>? writePrompt = null)
{
    private readonly Func<ConsoleKeyInfo> readKeyFunc = readKey ?? (() => Console.ReadKey(intercept: true));
    private readonly Action<string> writePromptAction = writePrompt ?? Console.Write;

    /// <summary>
    /// Logs all planned actions for a what-if run without performing any writes.
    /// </summary>
    public void LogPlannedActions<TItem>(string jobKey, IReadOnlyList<SyncAction<TItem>> actions) where TItem : CanonicalItem
    {
        foreach (var action in actions)
        {
            logger.LogInformation("What-If: {Action}", action.ToDisplayString());
            logger.LogDebug("Job {JobKey}: planned action details: {ActionDetails}", jobKey, action.ToString());
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
    /// <returns>
    /// <c>true</c> when confirm mode remains enabled for subsequent actions; <c>false</c> when
    /// the user selected <c>A</c> to approve the remaining actions for the current run.
    /// </returns>
    /// <exception cref="OperationFaultedException">
    /// Thrown only when a transient failure aborts the pass.
    /// </exception>
    public async Task<bool> ApplyActionsAsync<TItem>(
        IReadOnlyList<SyncAction<TItem>> actions,
        SyncDirection direction,
        Job<TItem> job,
        bool confirm,
        CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        await EnsureActionItemsLoadedAsync(actions, cancellationToken);

        string entityType = job.Options.EntityType;
        var targetConnector = direction == SourceToDestination ? job.DestinationConnector : job.SourceConnector;

        bool failedActions = false;

        foreach (var action in actions)
        {
            logger.LogDebug("Job {JobKey}: action details: {ActionDetails}", job.Key, action.ToString());

            if (confirm)
            {
                switch (PromptForConfirmation(action))
                {
                    case 'N':
                        continue;
                    case 'A':
                        confirm = false;
                        break;
                    case 'Q':
                        throw new OperationFaultedException("Action execution was aborted by the user.");
                }
            }

            logger.LogInformation("Executing: {Action}", action.ToDisplayString());
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
            else
            {
                logger.LogInformation("Done: {Action}", action.ToDisplayString());
            }
        }

        if (failedActions)
        {
            logger.LogWarning(
                "Job {JobKey}: one or more synchronization actions failed. Check the log messages above for details.",
                job.Key);
        }

        return confirm;
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
        try
        {
            switch (action.Kind)
            {
                case Create:
                    {
                        var originItem = action.GetOriginItem()
                            ?? throw new InvalidOperationException($"Create action has no origin item (job '{job.Key}', direction {direction}).");

                        var createdItem = await targetConnector.CreateItemAsync(originItem, cancellationToken);

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

                case Update:
                    {
                        var originItem = action.GetOriginItem()
                            ?? throw new InvalidOperationException($"Update action has no origin item (job '{job.Key}', direction {direction}).");

                        // matchedTargetItem may be null on delta runs for persisted links: the target
                        // side was implicitly unchanged and not returned by the connector.
                        // For inferred links both items are always loaded, so null here is a bug.
                        var matchedTargetItem = action.GetTargetItem();
                        var link = action.Link.PersistedState;

                        if (link is null && matchedTargetItem is null)
                        {
                            throw new InvalidOperationException(
                                $"Update action has neither a persisted link nor a live target item (job '{job.Key}', direction {direction}).");
                        }

                        var targetItemToWrite = CreateTargetItemToWrite(originItem, link, direction, matchedTargetItem);

                        DetachPhotoIfUnchanged(action, targetItemToWrite);

                        var updatedTargetItem = await targetConnector.UpdateItemAsync(targetItemToWrite, cancellationToken);

                        if (!job.Options.NoPersistence)
                        {
                            await opLog.AppendAsync(job.Key, entityType, "update", updatedTargetItem.Provenance.ProviderId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            if (link is not null)
                            {
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
                            else
                            {
                                // Inferred link (no prior persisted state): create the link row now.
                                var newLink = new LinkStateRow
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
                                await linkStateRepo.UpsertAsync(newLink, cancellationToken);
                            }
                        }

                        break;
                    }

                case Delete:
                    {
                        // matchedTargetItem is null on delta runs: the target was implicitly unchanged
                        // and not returned by the connector. Fall back to the persisted ID.
                        var matchedTargetItem = action.GetTargetItem();
                        var persistedLink = action.Link.PersistedState;
                        string deleteId = matchedTargetItem?.Provenance.ProviderId
                            ?? (direction == SourceToDestination ? persistedLink?.DestinationId : persistedLink?.SourceId)
                            ?? throw new InvalidOperationException(
                                $"Delete action has no target provider ID — neither a live target item nor a persisted link row with a target ID is available (job '{job.Key}', direction {direction}).");

                        await targetConnector.DeleteItemAsync(deleteId, cancellationToken);

                        if (!job.Options.NoPersistence)
                        {
                            await opLog.AppendAsync(job.Key, entityType, "delete", deleteId, direction.ToString(), "ok", cancellationToken: cancellationToken);

                            if (persistedLink is not null)
                            {
                                if (direction == SourceToDestination)
                                {
                                    persistedLink.DestinationDeleted = true;
                                }
                                else
                                {
                                    persistedLink.SourceDeleted = true;
                                }

                                persistedLink.LastSyncedAt = DateTimeOffset.UtcNow;
                                persistedLink.LastSyncResult = "deleted";
                                await linkStateRepo.UpsertAsync(persistedLink, cancellationToken);
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
            string itemId = action.GetOriginItem()?.Provenance.ProviderId
                ?? action.GetTargetItem()?.Provenance.ProviderId
                ?? action.Link.PersistedState?.SourceId
                ?? "unknown";
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
                "Job {JobKey}: transient failure applying {Operation} for item {ItemId} on side {Side}; aborting run. Error: {ErrorMessage}",
                jobKey,
                operation,
                itemId,
                direction,
                ex.Message);
            logger.LogDebug(
                ex,
                "Job {JobKey}: transient exception details for {Operation} on item {ItemId} ({Side})",
                jobKey,
                operation,
                itemId,
                direction);

            throw new OperationFaultedException(
                $"Transient failure applying {operation} for item '{itemId}' on side {direction}.",
                ex);
        }

        logger.LogError(
            "Job {JobKey}: failed to apply {Operation} for item {ItemId} on side {Side}; continuing. Error: {ErrorMessage}",
            jobKey,
            operation,
            itemId,
            direction,
            ex.Message);
        logger.LogDebug(
            ex,
            "Job {JobKey}: exception details for failed {Operation} on item {ItemId} ({Side})",
            jobKey,
            operation,
            itemId,
            direction);

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

    private char PromptForConfirmation<TItem>(SyncAction<TItem> action) where TItem : CanonicalItem
    {
        while (true)
        {
            writePromptAction($"Planned action: {action.ToDisplayString()}{Environment.NewLine}Proceed? [Y]es [N]o [A]ll [Q]uit: ");
            var key = readKeyFunc();
            Console.WriteLine();
            switch (key.KeyChar)
            {
                case 'Y' or 'y':
                    return 'Y';
                case 'N' or 'n':
                    return 'N';
                case 'A' or 'a':
                    return 'A';
                case 'Q' or 'q':
                    return 'Q';
            }
        }
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
