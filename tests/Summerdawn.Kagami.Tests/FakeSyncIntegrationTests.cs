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
    private readonly JobExecutor eventExecutor;
    private readonly FakeConnector sourceConnector = new();
    private readonly FakeConnector destinationConnector = new();
    private readonly FakeEventConnector sourceEventConnector = new();
    private readonly FakeEventConnector destinationEventConnector = new();

    public FakeSyncIntegrationTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        linkStateRepository = new LinkStateRepository(db);
        endpointCursorRepository = new EndpointCursorRepository(db);
        leaseRepository = new LeaseRepository(db);
        operationLogRepository = new OperationLogRepository(db);
        executor = new JobExecutor(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            linkStateRepository,
            endpointCursorRepository,
            leaseRepository,
            new SyncActionExecutor(linkStateRepository, operationLogRepository, NullLogger<SyncActionExecutor>.Instance),
            db,
            NullLogger<JobExecutor>.Instance);
        eventExecutor = new JobExecutor(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            linkStateRepository,
            endpointCursorRepository,
            leaseRepository,
            new SyncActionExecutor(linkStateRepository, operationLogRepository, NullLogger<SyncActionExecutor>.Instance),
            db,
            NullLogger<JobExecutor>.Instance);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task WhatIf_PlansWithoutWritingState()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));

        var result = await executor.ExecuteJobAsync(CreateJob("job-1"), whatIf: true);

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Empty(await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB"));
    }

    [Fact]
    public async Task InitialSync_CreatesLinkState()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));

        var result = await executor.ExecuteJobAsync(CreateJob("job-1"));

        Assert.True(result.Succeeded);
        var links = await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB");
        Assert.Single(links);
        Assert.Equal("a1", links[0].SourceId);
        Assert.NotNull(links[0].DestinationId);
    }

    [Fact]
    public async Task ExistingLease_SkipsExecution()
    {
        await leaseRepository.TryAcquireAsync("job-1");

        var result = await executor.ExecuteJobAsync(CreateJob("job-1"));

        Assert.True(result.Skipped);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task IncrementalRerun_DoesNotCreateDuplicateLinks()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));
        await executor.ExecuteJobAsync(CreateJob("job-1"));

        var firstLinks = await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB");
        await executor.ExecuteJobAsync(CreateJob("job-1"));
        var secondLinks = await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB");

        Assert.Equal(firstLinks.Count, secondLinks.Count);
    }

    [Fact]
    public async Task MirroredDeletion_DoesNotRepeatTargetDeleteAfterTombstoneIsConsumed()
    {
        sourceConnector.Seed(CreateContact("microsoft-1", "Meeting"));
        await executor.ExecuteJobAsync(CreateJob("job-1"));

        string googleId = Assert.Single(destinationConnector.Items).Provenance.ProviderId;
        destinationConnector.MarkDeleted(googleId);

        var deletionRun = await executor.ExecuteJobAsync(CreateJob("job-1"));
        var linkAfterDeletion = Assert.Single(await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB"));
        var rerun = await executor.ExecuteJobAsync(CreateJob("job-1"));

        Assert.True(deletionRun.Succeeded);
        Assert.True(linkAfterDeletion.SourceDeleted);
        Assert.True(linkAfterDeletion.DestinationDeleted);
        Assert.True(rerun.Succeeded);
        Assert.Equal(0, rerun.ActionsPlanned);
    }

    [Fact]
    public async Task MirroredEventDeletion_DoesNotRepeatTargetDeleteAfterTombstoneIsConsumed()
    {
        sourceEventConnector.Seed(CreateEvent("microsoft-event-1", "Meeting"));
        await eventExecutor.ExecuteJobAsync(CreateEventJob("event-job-1"));

        string googleId = Assert.Single(destinationEventConnector.Items).Provenance.ProviderId;
        destinationEventConnector.MarkDeleted(googleId);

        var deletionRun = await eventExecutor.ExecuteJobAsync(CreateEventJob("event-job-1"));
        var linkAfterDeletion = Assert.Single(await linkStateRepository.GetByPartitionAsync("events:endpointA:endpointB"));
        var rerun = await eventExecutor.ExecuteJobAsync(CreateEventJob("event-job-1"));

        Assert.True(deletionRun.Succeeded);
        Assert.True(linkAfterDeletion.SourceDeleted);
        Assert.True(linkAfterDeletion.DestinationDeleted);
        Assert.True(rerun.Succeeded);
        Assert.Equal(0, rerun.ActionsPlanned);
    }

    [Fact]
    public async Task ForwardUpdate_RefreshesBothVersionBaselines()
    {
        sourceConnector.Seed(CreateContact("a1", "Meeting"));
        destinationConnector.Seed(CreateContact("b1", "Meeting"));

        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contacts:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "0",
            DestinationVersion = "0",
        });

        await sourceConnector.UpdateItemAsync(CreateContact("a1", "Updated Meeting"));

        var firstRun = await executor.ExecuteJobAsync(CreateJob("job-1"));
        var linksAfterUpdate = await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB");
        var secondRun = await executor.ExecuteJobAsync(CreateJob("job-1"));
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

    private Job<CanonicalEvent> CreateEventJob(string jobKey, SyncMode mode = SyncMode.Bidirectional) =>
        new(jobKey, CreateJobOptions(mode), sourceEventConnector, destinationEventConnector);

    private static JobOptions CreateJobOptions(SyncMode mode = SyncMode.Bidirectional) => new()
    {
        SourceEndpointName = "endpointA",
        DestinationEndpointName = "endpointB",
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

    private static CanonicalEvent CreateEvent(string id, string title) => new()
    {
        Title = title,
        From = new DateTimeOffset(2026, 05, 01, 10, 00, 00, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 05, 01, 11, 00, 00, TimeSpan.Zero),
        ICalUid = $"{id}@example.invalid",
        Provenance =
        {
            ProviderId = id,
            Version = "v1",
        }
    };
}
