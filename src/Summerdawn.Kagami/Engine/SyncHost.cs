using Microsoft.Extensions.DependencyInjection;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Orchestrates multiple sync jobs, managing concurrency and dispatching each job to <see cref="JobExecutor"/>.
/// </summary>
public sealed class SyncHost(
    KagamiOptions options,
    IServiceProvider provider,
    JobExecutor executor,
    StateDatabase stateDb,
    ILogger<SyncHost> logger)
{
    /// <summary>
    /// Runs the enabled jobs once and returns.
    /// </summary>
    public Task RunOnceAsync(bool whatIf = false, string? jobKeyFilter = null, CancellationToken cancellationToken = default) =>
        RunOnceAsync(whatIf ? JobExecutionFlags.WhatIf : JobExecutionFlags.None, jobKeyFilter, cancellationToken);

    /// <summary>
    /// Runs the enabled jobs once and returns.
    /// </summary>
    public async Task RunOnceAsync(JobExecutionFlags executionFlags, string? jobKeyFilter = null, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        var jobs = GetEnabledJobs(jobKeyFilter);
        logger.LogInformation("RunOnce: executing {Count} job(s), whatIf={WhatIf}, confirm={Confirm}", jobs.Count, executionFlags.HasFlag(JobExecutionFlags.WhatIf), executionFlags.HasFlag(JobExecutionFlags.Confirm));
        await ExecuteJobsAsync(jobs, executionFlags, cancellationToken);
    }

    /// <summary>
    /// Dispatches each job to <see cref="JobExecutor"/> concurrently, bounded by <see cref="KagamiHostOptions.MaxConcurrentJobs"/>.
    /// </summary>
    private async Task ExecuteJobsAsync(
        IReadOnlyList<KeyValuePair<string, JobOptions>> jobs,
        JobExecutionFlags executionFlags,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, options.Host.MaxConcurrentJobs));
        var tasks = jobs.Select(async kvp =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await ExecuteJobAsync(kvp.Key, kvp.Value, executionFlags, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Force-releases all job leases and returns the number cleared.
    /// </summary>
    public async Task<int> UnlockAllJobsAsync(CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        var leaseRepo = new LeaseRepository(stateDb);
        int cleared = await leaseRepo.ForceReleaseAllAsync(cancellationToken);
        logger.LogInformation("Force-released {Count} job lease(s)", cleared);
        return cleared;
    }

    /// <summary>
    /// Force-releases the lease for a specific contacts sync, identified by the source and destination endpoint names.
    /// </summary>
    /// <returns>The number of leases cleared (0 or 1).</returns>
    public async Task<int> UnlockContactsSyncAsync(string fromEndpoint, string toEndpoint, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        string jobKey = $"contacts:{fromEndpoint}:{toEndpoint}";
        var leaseRepo = new LeaseRepository(stateDb);
        int cleared = await leaseRepo.ForceReleaseAsync(jobKey, cancellationToken);
        logger.LogInformation("Force-released lease for contacts sync {FromEndpoint} → {ToEndpoint}: {Count} cleared", fromEndpoint, toEndpoint, cleared);
        return cleared;
    }

    /// <summary>
    /// Resets the sync state for the specified job, clearing its link rows and saved cursors.
    /// </summary>
    public async Task ResetJobAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        if (!options.Jobs.TryGetValue(jobKey, out var job))
        {
            throw new InvalidOperationException($"Job '{jobKey}' not found in configuration.");
        }

        string partitionKey = $"{job.EntityType}:{job.SourceEndpointName}:{job.DestinationEndpointName}";
        var linkRepo = new LinkStateRepository(stateDb);
        await linkRepo.DeleteByPartitionAsync(partitionKey, cancellationToken);
        var cursorRepo = new EndpointCursorRepository(stateDb);
        await cursorRepo.DeleteCursorAsync(jobKey, job.SourceEndpointName, cancellationToken);
        await cursorRepo.DeleteCursorAsync(jobKey, job.DestinationEndpointName, cancellationToken);
        logger.LogInformation("Reset job {JobKey}: link state and cursors cleared", jobKey);
    }

    /// <summary>
    /// Resets the sync state for a specific contacts sync, identified by the source and destination endpoint names.
    /// Clears the link-state partition and saved cursors for the <c>contacts:{from}:{to}</c> job key.
    /// </summary>
    public async Task ResetContactsSyncAsync(string fromEndpoint, string toEndpoint, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        string partitionKey = $"contact:{fromEndpoint}:{toEndpoint}";
        string jobKey = $"contacts:{fromEndpoint}:{toEndpoint}";
        var linkRepo = new LinkStateRepository(stateDb);
        await linkRepo.DeleteByPartitionAsync(partitionKey, cancellationToken);
        var cursorRepo = new EndpointCursorRepository(stateDb);
        await cursorRepo.DeleteCursorAsync(jobKey, fromEndpoint, cancellationToken);
        await cursorRepo.DeleteCursorAsync(jobKey, toEndpoint, cancellationToken);
        logger.LogInformation("Reset contacts sync {FromEndpoint} → {ToEndpoint}: link state and cursors cleared", fromEndpoint, toEndpoint);
    }

    /// <summary>
    /// Resets all sync state by deleting all link-state rows and all stored cursors.
    /// </summary>
    public async Task ResetAllAsync(CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        var linkRepo = new LinkStateRepository(stateDb);
        await linkRepo.DeleteAllAsync(cancellationToken);
        var cursorRepo = new EndpointCursorRepository(stateDb);
        await cursorRepo.DeleteAllAsync(cancellationToken);
        logger.LogInformation("Reset all: all link state and cursors cleared");
    }

    /// <summary>
    /// Dispatches the job to the correct typed <see cref="ExecuteJobAsync{TItem}"/> overload based on <see cref="JobOptions.EntityType"/>.
    /// </summary>
    private Task ExecuteJobAsync(string jobKey, JobOptions jobOptions, JobExecutionFlags executionFlags, CancellationToken cancellationToken)
    {
        try
        {
            return jobOptions.EntityType switch
            {
                EntityType.Contact => ExecuteJobAsync<CanonicalContact>(jobKey, jobOptions, executionFlags, cancellationToken),
                EntityType.CalendarEvent => ExecuteJobAsync<CanonicalCalendarEvent>(jobKey, jobOptions, executionFlags, cancellationToken),
                _ => throw new InvalidOperationException($"Unknown entity type {jobOptions.EntityType}.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobKey} failed: {Message}", jobKey, ex.Message);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Resolves the connectors for a typed job and delegates execution to the runtime-flags job executor overload.
    /// </summary>
    private async Task ExecuteJobAsync<TItem>(string jobKey, JobOptions jobOptions, JobExecutionFlags executionFlags, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        try
        {
            var connectorResolver = provider.GetRequiredService<Func<string, IConnector<TItem>>>();
            var sourceConnector = connectorResolver(jobOptions.SourceEndpointName);
            var destinationConnector = connectorResolver(jobOptions.DestinationEndpointName);

            var job = new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector);

            await executor.ExecuteJobAsync(job, executionFlags, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobKey} failed: {Message}", jobKey, ex.Message);
        }
    }

    /// <summary>
    /// Returns the enabled jobs, optionally filtered to a single job key.
    /// </summary>
    private List<KeyValuePair<string, JobOptions>> GetEnabledJobs(string? jobKeyFilter) =>
        options.Jobs
            .Where(kvp => kvp.Value.Enabled && (jobKeyFilter is null || kvp.Key == jobKeyFilter))
            .ToList();
}
