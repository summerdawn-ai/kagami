using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class FakeSyncIntegrationTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly LinkStateRepository linkStateRepository;
    private readonly EndpointCursorRepository endpointCursorRepository;
    private readonly LeaseRepository leaseRepository;
    private readonly OperationLogRepository operationLogRepository;
    private readonly JobExecutor executor;
    private readonly FakeConnector sourceConnector = new();
    private readonly FakeConnector destinationConnector = new();

    public FakeSyncIntegrationTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        linkStateRepository = new LinkStateRepository(db);
        endpointCursorRepository = new EndpointCursorRepository(db);
        leaseRepository = new LeaseRepository(db);
        operationLogRepository = new OperationLogRepository(db);
        executor = new JobExecutor(
            new Planner(NullLogger<Planner>.Instance),
            linkStateRepository,
            endpointCursorRepository,
            operationLogRepository,
            leaseRepository,
            NullLogger<JobExecutor>.Instance);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task WhatIf_PlansWithoutWritingState()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));

        var result = await executor.ExecuteAsync(CreateJob("job-1"), whatIf: true);

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Empty(await linkStateRepository.GetByJobAsync("job-1"));
    }

    [Fact]
    public async Task InitialSync_CreatesLinkState()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));

        var result = await executor.ExecuteAsync(CreateJob("job-1"));

        Assert.True(result.Succeeded);
        var links = await linkStateRepository.GetByJobAsync("job-1");
        Assert.Single(links);
        Assert.Equal("a1", links[0].SourceId);
        Assert.NotNull(links[0].DestinationId);
    }

    [Fact]
    public async Task ExistingLease_SkipsExecution()
    {
        await leaseRepository.TryAcquireAsync("job-1", "external-holder", TimeSpan.FromMinutes(5));

        var result = await executor.ExecuteAsync(CreateJob("job-1"));

        Assert.True(result.Skipped);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task IncrementalRerun_DoesNotCreateDuplicateLinks()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        var firstLinks = await linkStateRepository.GetByJobAsync("job-1");
        await executor.ExecuteAsync(CreateJob("job-1"));
        var secondLinks = await linkStateRepository.GetByJobAsync("job-1");

        Assert.Equal(firstLinks.Count, secondLinks.Count);
    }

    [Fact]
    public async Task ForwardUpdate_RefreshesBothVersionBaselines()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));
        destinationConnector.Seed(CreateContact("b1", "Meeting"));

        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.Contact,
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "0",
            DestinationVersion = "0",
        });

        await sourceConnector.UpdateItemAsync(CreateContact("a1", "Updated Meeting"));

        var firstRun = await executor.ExecuteAsync(CreateJob("job-1"));
        var linksAfterUpdate = await linkStateRepository.GetByJobAsync("job-1");
        var secondRun = await executor.ExecuteAsync(CreateJob("job-1"));
        var currentSource = await sourceConnector.GetItemAsync("a1");
        var currentTarget = await destinationConnector.GetItemAsync("b1");

        Assert.True(firstRun.Succeeded);
        Assert.Single(linksAfterUpdate);
        Assert.Equal(currentSource!.Provenance.Version, linksAfterUpdate[0].SourceVersion);
        Assert.Equal(currentTarget!.Provenance.Version, linksAfterUpdate[0].DestinationVersion);
        Assert.Equal(0, secondRun.ActionsPlanned);
    }

    private Job<CanonicalContact> CreateJob(string jobKey, SyncMode mode = SyncMode.Bidirectional) =>
        new(jobKey, CreateJobOptions(mode), sourceConnector, destinationConnector);

    private static JobOptions CreateJobOptions(SyncMode mode = SyncMode.Bidirectional) => new()
    {
        Enabled = true,
        EntityType = EntityType.Contact,
        Source = "endpointA",
        Destination = "endpointB",
        SyncMode = mode,
        DeletePolicy = DeletePolicy.Mirror,
        ConflictPolicy = ConflictPolicy.LastWriteWins,
    };

    private static CanonicalContact CreateContact(string id, string displayName) => new()
    {
        DisplayName = displayName,

        Provenance =
        {
            ProviderId = id,
            Version = "v1",
        }
    };
}
