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
        var page = await connector.GetInitialPageAsync(cancellationToken);

        while (true)
        {
            foreach (var item in page.Items)
            {
                if (filter is not null && !filter.Matches(item))
                {
                    continue;
                }

                if (item is CanonicalContact contact)
                {
                    await ContactPhotoLoader.EnsureLoadedAsync(contact, cancellationToken);
                }

                items.Add(item);
                if (maxItems is not null && items.Count >= maxItems.Value)
                {
                    return items;
                }
            }

            if (!page.HasMore)
            {
                return items;
            }

            if (page.NextCursor is null)
            {
                throw new InvalidOperationException($"Connector returned HasMore=true without a cursor for endpoint '{endpointName}'.");
            }

            page = await connector.GetIncrementalPageAsync(page.NextCursor, cancellationToken);
        }
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
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = endpointName,
            Destination = outputDirectory,
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

        // Derive a stable job key from the two endpoint names so that sync state
        // is persisted consistently across invocations of the same contacts sync command.
        string jobKey = $"contacts:{fromEndpoint}:{toEndpoint}";

        var jobOptions = new JobOptions
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = fromEndpoint,
            Destination = toEndpoint,
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
            Full = full,
            Force = force,
            Filter = filter?.Scope,
        };

        return await jobExecutor.ExecuteJobAsync(new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector),
            whatIf,
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
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = sourceDirectory,
            Destination = toEndpoint,
            SyncMode = SyncMode.Forward,
            DeletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore,
            Full = true,
            Force = force,
            Filter = filter?.Scope,
            NoPersistence = true,
        };

        logger.LogInformation("Starting import from {SourceDir} to {Endpoint} (prune={Prune}, whatIf={WhatIf})", sourceDirectory, toEndpoint, prune, whatIf);

        return await jobExecutor.ExecuteJobAsync(
            new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector),
            whatIf,
            cancellationToken);
    }

    /// <summary>
    /// Resolves and returns a connector for the named endpoint.
    /// </summary>
    private IConnector<TItem> BuildConnector(string endpointName) => connectorResolver(endpointName);
}
