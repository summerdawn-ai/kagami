namespace Summerdawn.Kagami.Engine;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Persistence;

/// <summary>
/// Orchestrates multiple sync jobs: builds connectors, manages concurrency, and dispatches to JobExecutor.
/// </summary>
public sealed class SyncHost(
    KagamiOptions options,
    IConnectorFactory connectorFactory,
    JobExecutor executor,
    StateDatabase stateDb,
    ILogger<SyncHost> logger)
{
    /// <summary>Runs the enabled jobs once and returns, optionally filtered to a single job.</summary>
    public async Task RunOnceAsync(bool whatIf = false, string? jobKeyFilter = null, CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        var jobs = GetEnabledJobs(jobKeyFilter);
        logger.LogInformation("RunOnce: executing {Count} job(s), whatIf={WhatIf}", jobs.Count, whatIf);
        await ExecuteJobsAsync(jobs, whatIf, cancellationToken);
    }

    /// <summary>Runs continuously, polling the enabled jobs on their configured schedules.</summary>
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

    /// <summary>Force-releases all job leases. Returns the number of locks cleared.</summary>
    public async Task<int> UnlockAllJobsAsync(CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);
        var leaseRepo = new LeaseRepository(stateDb);
        int cleared = await leaseRepo.ForceReleaseAllAsync(cancellationToken);
        logger.LogInformation("Force-released {Count} job lease(s)", cleared);
        return cleared;
    }

    /// <summary>Resets the sync state for the given job key.</summary>
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
        await cursorRepo.DeleteCursorAsync(jobKey, job.EndpointA, cancellationToken);
        await cursorRepo.DeleteCursorAsync(jobKey, job.EndpointB, cancellationToken);
        logger.LogInformation("Reset job {JobKey}: link state and cursors cleared", jobKey);
    }

    private async Task ExecuteJobAsync(string jobKey, JobOptions jobOptions, bool whatIf, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.Endpoints.TryGetValue(jobOptions.EndpointA, out var endpointA))
            {
                throw new InvalidOperationException($"Endpoint '{jobOptions.EndpointA}' not found.");
            }

            if (!options.Endpoints.TryGetValue(jobOptions.EndpointB, out var endpointB))
            {
                throw new InvalidOperationException($"Endpoint '{jobOptions.EndpointB}' not found.");
            }

            options.Credentials.TryGetValue(endpointA.Credential, out var credA);
            options.Credentials.TryGetValue(endpointB.Credential, out var credB);
            var connA = connectorFactory.Create(jobOptions.EndpointA, endpointA, credA);
            var connB = connectorFactory.Create(jobOptions.EndpointB, endpointB, credB);
            await executor.ExecuteAsync(jobKey, jobOptions, connA, connB, whatIf, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobKey} failed: {Message}", jobKey, ex.Message);
        }
    }

    private List<KeyValuePair<string, JobOptions>> GetEnabledJobs(string? jobKeyFilter) =>
        options.Jobs
            .Where(kvp => kvp.Value.Enabled && (jobKeyFilter is null || kvp.Key == jobKeyFilter))
            .ToList();

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
