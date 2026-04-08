namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

public sealed class FakeSyncIntegrationTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly LinkStateRepository linkStateRepository;
    private readonly EndpointCursorRepository endpointCursorRepository;
    private readonly LeaseRepository leaseRepository;
    private readonly OperationLogRepository operationLogRepository;
    private readonly JobExecutor executor;
    private readonly FakeConnector connectorA = new();
    private readonly FakeConnector connectorB = new();

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
    public async Task WhatIfPlansWithoutWritingState()
    {
        connectorA.Seed(CreateEvent("a1", "Meeting"));

        var result = await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB, whatIf: true);

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Empty(await linkStateRepository.GetByJobAsync("job-1"));
    }

    [Fact]
    public async Task InitialSyncCreatesLinkState()
    {
        connectorA.Seed(CreateEvent("a1", "Meeting"));

        var result = await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB);

        Assert.True(result.Succeeded);
        var links = await linkStateRepository.GetByJobAsync("job-1");
        Assert.Single(links);
        Assert.Equal("a1", links[0].SideAId);
        Assert.NotNull(links[0].SideBId);
    }

    [Fact]
    public async Task ExistingLeaseSkipsExecution()
    {
        await leaseRepository.TryAcquireAsync("job-1", "external-holder", TimeSpan.FromMinutes(5));

        var result = await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB);

        Assert.True(result.Skipped);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task IncrementalRerunDoesNotCreateDuplicateLinks()
    {
        connectorA.Seed(CreateEvent("a1", "Meeting"));
        await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB);

        var firstLinks = await linkStateRepository.GetByJobAsync("job-1");
        await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB);
        var secondLinks = await linkStateRepository.GetByJobAsync("job-1");

        Assert.Equal(firstLinks.Count, secondLinks.Count);
    }

    private static JobOptions CreateJob(SyncMode mode = SyncMode.Bidirectional) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.CalendarEvent,
            EndpointA = "endpointA",
            EndpointB = "endpointB",
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
