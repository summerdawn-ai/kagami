
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
        sourceConnector.Seed(CreateEvent("a1", "Meeting"));

        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, whatIf: true);

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Empty(await linkStateRepository.GetByJobAsync("job-1"));
    }

    [Fact]
    public async Task InitialSync_CreatesLinkState()
    {
        sourceConnector.Seed(CreateEvent("a1", "Meeting"));

        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);

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

        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);

        Assert.True(result.Skipped);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task IncrementalRerun_DoesNotCreateDuplicateLinks()
    {
        sourceConnector.Seed(CreateEvent("a1", "Meeting"));
        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);

        var firstLinks = await linkStateRepository.GetByJobAsync("job-1");
        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);
        var secondLinks = await linkStateRepository.GetByJobAsync("job-1");

        Assert.Equal(firstLinks.Count, secondLinks.Count);
    }

    [Fact]
    public async Task ForwardUpdate_RefreshesBothVersionBaselines()
    {
        sourceConnector.Seed(CreateEvent("a1", "Meeting"));
        destinationConnector.Seed(CreateEvent("b1", "Meeting"));

        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
        });

        await sourceConnector.UpdateItemAsync(CreateEvent("a1", "Updated Meeting"));

        var firstRun = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);
        var linksAfterUpdate = await linkStateRepository.GetByJobAsync("job-1");
        var secondRun = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);
        var currentSource = await sourceConnector.GetItemAsync("a1");
        var currentTarget = await destinationConnector.GetItemAsync("b1");

        Assert.True(firstRun.Succeeded);
        Assert.Single(linksAfterUpdate);
        Assert.Equal(currentSource!.Version, linksAfterUpdate[0].SourceVersion);
        Assert.Equal(currentTarget!.Version, linksAfterUpdate[0].DestinationVersion);
        Assert.Equal(0, secondRun.ActionsPlanned);
    }

    private static JobOptions CreateJob(SyncMode mode = SyncMode.Bidirectional) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.CalendarEvent,
            Source = "endpointA",
            Destination = "endpointB",
            SyncMode = mode,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = ConflictPolicy.LastWriteWins,
        };

    private static CanonicalItem CreateEvent(string id, string subject) =>
        new()
        {
            EntityType = EntityType.CalendarEvent,
            SourceId = id,
            Version = "v1",
            Payload = new CanonicalCalendarEvent
            {
                Subject = subject,
                Start = DateTimeOffset.UtcNow,
                End = DateTimeOffset.UtcNow.AddHours(1),
            },
        };
}
