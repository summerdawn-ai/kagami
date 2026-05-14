using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami;

/// <summary>
/// Provides high-level interactive operations: list, export, sync, and import.
/// </summary>
public sealed class CommandHandler<TItem>(
    Func<string, IConnector<TItem>> connectorResolver,
    JobExecutor jobExecutor,
    StateDatabase stateDb,
    ILogger<CommandHandler<TItem>> logger) where TItem : CanonicalItem
{
    /// <summary>
    /// Fetches items from the named endpoint and returns them in order, optionally applying a filter and item limit.
    /// </summary>
    public async Task<IReadOnlyList<TItem>> ListAsync(
        string endpointName,
        IFilter<TItem>? filter = null,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        if (maxItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Maximum item count must be zero or greater.");
        }

        var connector = BuildConnector(endpointName);
        await connector.AuthenticateAsync(cancellationToken);

        List<TItem> items = [];
        // For architectural reasons, load _all_ items, anyway;
        // the max count is only to avoid overfilling the output.
        var allItems = await connector.GetAllItemsAsync(cancellationToken);

        // Limit listed items to max count.
        foreach (var item in allItems)
        {
            if (filter is not null && !filter.Matches(item))
            {
                continue;
            }

            items.Add(item);
            if (maxItems is not null && items.Count >= maxItems.Value)
            {
                return items;
            }
        }

        return items;
    }

    /// <summary>
    /// Exports items from the named endpoint to a local directory as JSON files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One JSON file is written per item, using the contact name as the base filename.
    /// Existing files in <paramref name="outputDirectory"/> that no longer correspond to a
    /// source contact are only deleted when <paramref name="prune"/> is <c>true</c>; without
    /// pruning the directory is never clobbered.
    /// </para>
    /// </remarks>
    public async Task<JobExecutionResult> ExportAsync(
        string endpointName,
        string outputDirectory,
        bool prune = false,
        IFilter<TItem>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (typeof(TItem) != typeof(CanonicalContact))
        {
            throw new NotSupportedException("Export is only supported for contacts.");
        }

        await stateDb.InitializeAsync(cancellationToken);
        Directory.CreateDirectory(outputDirectory);

        var sourceConnector = BuildConnector(endpointName);
        var destinationConnector = (IConnector<TItem>)(object)new ImportExportContactsConnector(outputDirectory);

        string jobKey = $"contacts:export:{endpointName}:{outputDirectory}";

        var jobOptions = new JobOptions
        {
            EntityType = EntityType.Contact,
            SourceEndpointName = endpointName,
            DestinationEndpointName = outputDirectory,
            SyncMode = SyncMode.Forward,
            DeletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore,
            Full = true,
            Filter = filter?.Scope,
            NoPersistence = true,
        };

        return await jobExecutor.ExecuteJobAsync(
            new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector),
            whatIf: false,
            cancellationToken);
    }

    /// <summary>
    /// Synchronizes contacts between two configured endpoints.
    /// </summary>
    public async Task<JobExecutionResult> SyncAsync(
        string fromEndpoint,
        string toEndpoint,
        SyncMode mode = SyncMode.Forward,
        bool whatIf = false,
        bool confirm = false,
        IFilter<TItem>? filter = null,
        bool full = false,
        bool force = false,
        DeletePolicy deletePolicy = DeletePolicy.Ignore,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);

        var sourceConnector = BuildConnector(fromEndpoint);
        var destinationConnector = BuildConnector(toEndpoint);

        // Derive a stable job key from the entity type and endpoint names so that sync state
        // is persisted consistently across invocations. Contacts preserve the legacy key format
        // to avoid resetting existing users' cursors and link state.
        string jobKey = typeof(TItem) == typeof(CanonicalContact)
            ? $"contacts:{fromEndpoint}:{toEndpoint}"
            : $"calendar-event:{fromEndpoint}:{toEndpoint}";

        var jobOptions = new JobOptions
        {
            EntityType = typeof(TItem) == typeof(CanonicalContact) ? EntityType.Contact : EntityType.CalendarEvent,
            SourceEndpointName = fromEndpoint,
            DestinationEndpointName = toEndpoint,
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
            Full = full,
            Force = force,
            Filter = filter?.Scope,
        };

        var executionFlags = whatIf ? JobExecutionFlags.WhatIf : JobExecutionFlags.None;
        if (confirm)
        {
            executionFlags |= JobExecutionFlags.Confirm;
        }

        return await jobExecutor.ExecuteJobAsync(new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector),
            executionFlags,
            cancellationToken);
    }

    /// <summary>
    /// Imports items from a directory of JSON files into the named endpoint via the job pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local JSON directory is presented as the source connector.  Items are matched against
    /// existing destination contacts by name and identifier fields; no sync-DB state is read or
    /// written during the run.
    /// </para>
    /// <para>
    /// When <paramref name="prune"/> is <c>true</c>, destination contacts that do not match any
    /// imported item are deleted.  The <paramref name="whatIf"/> flag logs planned actions
    /// without performing any writes.
    /// </para>
    /// </remarks>
    public async Task<JobExecutionResult> ImportAsync(
        string sourceDirectory,
        string toEndpoint,
        bool prune = false,
        IFilter<TItem>? filter = null,
        bool whatIf = false,
        bool confirm = false,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (typeof(TItem) != typeof(CanonicalContact))
        {
            throw new NotSupportedException("Import is only supported for contacts.");
        }

        await stateDb.InitializeAsync(cancellationToken);

        var sourceConnector = (IConnector<TItem>)(object)new ImportExportContactsConnector(sourceDirectory);
        var destinationConnector = BuildConnector(toEndpoint);

        string jobKey = $"contacts:import:{sourceDirectory}:{toEndpoint}";

        var jobOptions = new JobOptions
        {
            EntityType = EntityType.Contact,
            SourceEndpointName = sourceDirectory,
            DestinationEndpointName = toEndpoint,
            SyncMode = SyncMode.Forward,
            DeletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore,
            Full = true,
            Force = force,
            Filter = filter?.Scope,
            NoPersistence = true,
        };

        logger.LogInformation("Starting import from {SourceDir} to {Endpoint} (prune={Prune}, whatIf={WhatIf}, confirm={Confirm})", sourceDirectory, toEndpoint, prune, whatIf, confirm);

        var executionFlags = whatIf ? JobExecutionFlags.WhatIf : JobExecutionFlags.None;
        if (confirm)
        {
            executionFlags |= JobExecutionFlags.Confirm;
        }

        return await jobExecutor.ExecuteJobAsync(
            new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector),
            executionFlags,
            cancellationToken);
    }

    /// <summary>
    /// Resolves and returns a connector for the named endpoint.
    /// </summary>
    private IConnector<TItem> BuildConnector(string endpointName) => connectorResolver(endpointName);
}
