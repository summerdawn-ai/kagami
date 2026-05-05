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
    public async Task RunOnceAsync(bool whatIf = false, string? jobKeyFilter = null, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        var jobs = GetEnabledJobs(jobKeyFilter);
        logger.LogInformation("RunOnce: executing {Count} job(s), whatIf={WhatIf}", jobs.Count, whatIf);
        await ExecuteJobsAsync(jobs, whatIf, cancellationToken);
    }

    /// <summary>
    /// Runs continuously, polling the enabled jobs on their configured schedules.
    /// </summary>
    /// <remarks>
    /// The scheduler loop checks at most every <see cref="KagamiHostOptions.SchedulerIntervalSeconds"/>
    /// seconds for jobs whose interval has elapsed since their last run.
    /// </remarks>
    public async Task RunContinuousAsync(bool whatIf = false, string? jobKeyFilter = null, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        logger.LogInformation(
            "Kagami run mode started (whatIf={WhatIf}, jobFilter={JobKeyFilter})",
            whatIf,
            jobKeyFilter ?? "<all>");

        var lastRun = new Dictionary<string, DateTimeOffset>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var dueJobs = GetEnabledJobs(jobKeyFilter)
                .Where(kvp => now - lastRun.GetValueOrDefault(kvp.Key, DateTimeOffset.MinValue) >= ParseInterval(kvp.Value.Schedule))
                .ToList();

            if (dueJobs.Count > 0)
            {
                foreach (var (jobKey, _) in dueJobs)
                {
                    lastRun[jobKey] = now;
                }

                await ExecuteJobsAsync(dueJobs, whatIf, cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Host.SchedulerIntervalSeconds), cancellationToken);
        }
    }

    /// <summary>
    /// Dispatches each job to <see cref="JobExecutor"/> concurrently, bounded by <see cref="KagamiHostOptions.MaxConcurrentJobs"/>.
    /// </summary>
    private async Task ExecuteJobsAsync(
        IReadOnlyList<KeyValuePair<string, JobOptions>> jobs,
        bool whatIf,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, options.Host.MaxConcurrentJobs));
        var tasks = jobs.Select(async kvp =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await ExecuteJobAsync(kvp.Key, kvp.Value, whatIf, cancellationToken);
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
    /// Resets the sync state for the specified job, clearing its link rows and saved cursors.
    /// </summary>
    public async Task ResetJobAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        if (!options.Jobs.TryGetValue(jobKey, out var job))
        {
            throw new InvalidOperationException($"Job '{jobKey}' not found in configuration.");
        }

        var linkRepo = new LinkStateRepository(stateDb);
        await linkRepo.DeleteByJobAsync(jobKey, cancellationToken);
        var cursorRepo = new EndpointCursorRepository(stateDb);
        await cursorRepo.DeleteCursorAsync(jobKey, job.Source, cancellationToken);
        await cursorRepo.DeleteCursorAsync(jobKey, job.Destination, cancellationToken);
        logger.LogInformation("Reset job {JobKey}: link state and cursors cleared", jobKey);
    }

    /// <summary>
    /// Dispatches the job to the correct typed <see cref="ExecuteJobAsync{TItem}"/> overload based on <see cref="JobOptions.EntityType"/>.
    /// </summary>
    private Task ExecuteJobAsync(string jobKey, JobOptions jobOptions, bool whatIf, CancellationToken cancellationToken)
    {
        try
        {
            return jobOptions.EntityType switch
            {
                EntityType.Contact => ExecuteJobAsync<CanonicalContact>(jobKey, jobOptions, whatIf, cancellationToken),
                EntityType.CalendarEvent => ExecuteJobAsync<CanonicalCalendarEvent>(jobKey, jobOptions, whatIf, cancellationToken),
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
    /// Resolves the connectors for a typed job and delegates execution to <see cref="JobExecutor.ExecuteAsync{TItem}"/>.
    /// </summary>
    private async Task ExecuteJobAsync<TItem>(string jobKey, JobOptions jobOptions, bool whatIf, CancellationToken cancellationToken) where TItem : CanonicalItem
    {
        try
        {
            var connectorResolver = provider.GetRequiredService<Func<string, IConnector<TItem>>>();
            var sourceConnector = connectorResolver(jobOptions.Source);
            var destinationConnector = connectorResolver(jobOptions.Destination);

            var job = new Job<TItem>(jobKey, jobOptions, sourceConnector, destinationConnector);

            await executor.ExecuteAsync(job, whatIf, cancellationToken);
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

    /// <summary>
    /// Parses a simplified ISO 8601 duration string (e.g. <c>PT15M</c>, <c>PT2H</c>, <c>PT30S</c>)
    /// into a <see cref="TimeSpan"/>. Returns 15 minutes for unrecognised formats.
    /// </summary>
    private static TimeSpan ParseInterval(string schedule)
    {
        if (schedule.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
        {
            string value = schedule[2..^1];
            char unit = schedule[^1];
            if (int.TryParse(value, out int n))
            {
                return unit switch
                {
                    'M' or 'm' => TimeSpan.FromMinutes(n),
                    'H' or 'h' => TimeSpan.FromHours(n),
                    'S' or 's' => TimeSpan.FromSeconds(n),
                    _ => TimeSpan.FromMinutes(15),
                };
            }
        }

        return TimeSpan.FromMinutes(15);
    }
}
