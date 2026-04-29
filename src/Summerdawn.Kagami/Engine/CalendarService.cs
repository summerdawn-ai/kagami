
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// High-level service for interactive calendar operations: list and sync.
/// </summary>
public sealed class CalendarService(
    Func<string, IConnector> connectorResolver,
    JobExecutor jobExecutor,
    StateDatabase stateDb,
    ILogger<CalendarService> logger)
{
    /// <summary>
    /// Fetches all calendar events from the named endpoint and returns them in order.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c> (e.g. <c>GoogleCalendar</c>).</param>
    /// <param name="maxItems">Optional maximum number of items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CanonicalItem>> ListAsync(
        string endpointName,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        if (maxItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Maximum item count must be zero or greater.");
        }

        var connector = connectorResolver(endpointName);
        await connector.AuthenticateAsync(cancellationToken);

        List<CanonicalItem> items = [];
        var page = await connector.GetInitialPageAsync(cancellationToken);

        while (true)
        {
            foreach (var item in page.Items)
            {
                if (item.IsDeleted)
                {
                    continue;
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
    /// Synchronizes calendar events between two configured endpoints.
    /// </summary>
    /// <param name="fromEndpoint">Name of the source endpoint.</param>
    /// <param name="toEndpoint">Name of the destination endpoint.</param>
    /// <param name="mode">Sync direction.</param>
    /// <param name="whatIf">When <c>true</c>, logs actions without writing any changes.</param>
    /// <param name="force">
    /// When <c>true</c>, bypasses the HasChanged short-circuit so all in-scope events are re-evaluated.
    /// </param>
    /// <param name="deletePolicy">Delete handling policy.</param>
    /// <param name="conflictPolicy">Conflict resolution policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JobExecutionResult> SyncAsync(
        string fromEndpoint,
        string toEndpoint,
        SyncMode mode = SyncMode.Forward,
        bool whatIf = false,
        bool force = false,
        DeletePolicy deletePolicy = DeletePolicy.Ignore,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);

        var sourceConnector = connectorResolver(fromEndpoint);
        var destinationConnector = connectorResolver(toEndpoint);

        string jobKey = $"calendar:{fromEndpoint}:{toEndpoint}";

        var jobOptions = new JobOptions
        {
            Enabled = true,
            EntityType = EntityType.CalendarEvent,
            Source = fromEndpoint,
            Destination = toEndpoint,
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
        };

        logger.LogInformation(
            "CalendarService.SyncAsync: {From} → {To}, mode={Mode}, whatIf={WhatIf}",
            fromEndpoint,
            toEndpoint,
            mode,
            whatIf);

        return await jobExecutor.ExecuteAsync(
            jobKey,
            jobOptions,
            sourceConnector,
            destinationConnector,
            whatIf,
            null,
            force,
            cancellationToken);
    }
}
