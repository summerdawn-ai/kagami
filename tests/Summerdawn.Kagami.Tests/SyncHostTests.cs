using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class SyncHostTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;

    public SyncHostTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task RunOnceAsync_IgnoresScheduleAndRunsOnlyFilteredJob()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();

        sourceA.Seed(CreateContact("a1", "Alice"));
        sourceB.Seed(CreateContact("b1", "Bob"));

        var host = CreateHost(sourceA, targetA, sourceB, targetB, schedulerIntervalSeconds: 3600);

        await host.RunOnceAsync(jobKeyFilter: "job-a");

        Assert.Single(targetA.Items, item => !item.IsDeleted);
        Assert.Empty(targetB.Items);
    }

    [Fact]
    public async Task RunContinuousAsync_CanPollSingleJob()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();

        sourceA.Seed(CreateContact("a1", "Alice"));
        sourceB.Seed(CreateContact("b1", "Bob"));

        var host = CreateHost(sourceA, targetA, sourceB, targetB, schedulerIntervalSeconds: 3600);

        using CancellationTokenSource cts = new();
        var runTask = host.RunContinuousAsync(jobKeyFilter: "job-a", cancellationToken: cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);

        Assert.Single(targetA.Items, item => !item.IsDeleted);
        Assert.Empty(targetB.Items);
    }

    [Fact]
    public async Task RunContinuousAsync_HonorsWhatIf()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();

        sourceA.Seed(CreateContact("a1", "Alice"));

        var host = CreateHost(sourceA, targetA, sourceB, targetB, schedulerIntervalSeconds: 3600);

        using CancellationTokenSource cts = new();
        var runTask = host.RunContinuousAsync(whatIf: true, jobKeyFilter: "job-a", cancellationToken: cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);

        Assert.Empty(targetA.Items);

        LinkStateRepository linkStateRepository = new(db);
        Assert.Empty(await linkStateRepository.GetByJobAsync("job-a"));
    }

    [Fact]
    public async Task ResetJobAsync_ClearsOnlyThatJobsCursorState()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();
        var host = CreateHost(sourceA, targetA, sourceB, targetB, schedulerIntervalSeconds: 3600);
        EndpointCursorRepository cursorRepository = new(db);

        await cursorRepository.SetCursorAsync("job-a", "endpointA1", "startswith(name,'A')", "cursor-a");
        await cursorRepository.SetCursorAsync("other-job", "endpointA1", string.Empty, "cursor-b");

        await host.ResetJobAsync("job-a");

        Assert.Null(await cursorRepository.GetCursorAsync("job-a", "endpointA1"));
        Assert.NotNull(await cursorRepository.GetCursorAsync("other-job", "endpointA1"));
    }

    private SyncHost CreateHost(
        FakeConnector sourceA,
        FakeConnector targetA,
        FakeConnector sourceB,
        FakeConnector targetB,
        int schedulerIntervalSeconds)
    {
        var connectors = new Dictionary<string, IConnector>(StringComparer.OrdinalIgnoreCase)
        {
            ["endpointA1"] = sourceA,
            ["endpointB1"] = targetA,
            ["endpointA2"] = sourceB,
            ["endpointB2"] = targetB,
        };

        KagamiOptions options = new()
        {
            Host = new KagamiHostOptions
            {
                MaxConcurrentJobs = 1,
                SchedulerIntervalSeconds = schedulerIntervalSeconds,
            },
            Endpoints =
            {
                ["endpointA1"] = new EndpointOptions { Type = "fake" },
                ["endpointB1"] = new EndpointOptions { Type = "fake" },
                ["endpointA2"] = new EndpointOptions { Type = "fake" },
                ["endpointB2"] = new EndpointOptions { Type = "fake" },
            },
            Jobs =
            {
                ["job-a"] = CreateJob("endpointA1", "endpointB1", "PT24H"),
                ["job-b"] = CreateJob("endpointA2", "endpointB2", "PT24H"),
            },
        };

        JobExecutor executor = new(
            new Planner(NullLogger<Planner>.Instance),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new OperationLogRepository(db),
            new LeaseRepository(db),
            NullLogger<JobExecutor>.Instance);

        return new SyncHost(
            options,
            name => connectors[name],
            executor,
            db,
            NullLogger<SyncHost>.Instance);
    }

    private static JobOptions CreateJob(string endpointA, string endpointB, string schedule) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = endpointA,
            Destination = endpointB,
            SyncMode = SyncMode.Bidirectional,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = ConflictPolicy.LastWriteWins,
            Schedule = schedule,
        };

    private static CanonicalItem CreateContact(string id, string displayName) =>
        new()
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Version = "v1",
            Payload = new CanonicalContact { DisplayName = displayName },
        };
}
