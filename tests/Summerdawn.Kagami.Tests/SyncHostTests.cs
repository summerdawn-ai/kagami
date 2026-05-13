using Microsoft.Extensions.DependencyInjection;
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
    public async Task RunOnceAsync_RunsOnlyFilteredJob()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();

        sourceA.Seed(CreateContact("a1", "Alice"));
        sourceB.Seed(CreateContact("b1", "Bob"));

        var host = CreateHost(sourceA, targetA, sourceB, targetB);

        await host.RunOnceAsync(jobKeyFilter: "job-a");

        Assert.Single(targetA.Items, item => !item.IsDeleted);
        Assert.Empty(targetB.Items);
    }

    [Fact]
    public async Task ResetJobAsync_ClearsOnlyThatJobsCursorState()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();
        var host = CreateHost(sourceA, targetA, sourceB, targetB);
        EndpointCursorRepository cursorRepository = new(db);

        await cursorRepository.SetCursorAsync("job-a", "endpointA1", "startswith(name,'A')", "cursor-a");
        await cursorRepository.SetCursorAsync("other-job", "endpointA1", string.Empty, "cursor-b");

        await host.ResetJobAsync("job-a");

        Assert.Null(await cursorRepository.GetCursorAsync("job-a", "endpointA1"));
        Assert.NotNull(await cursorRepository.GetCursorAsync("other-job", "endpointA1"));
    }

    [Fact]
    public async Task ResetContactsSyncAsync_ClearsLinkStateAndCursorsForThatPair()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();
        var host = CreateHost(sourceA, targetA, sourceB, targetB);

        var cursorRepo = new EndpointCursorRepository(db);
        var linkRepo = new LinkStateRepository(db);

        // Seed some state for the contacts:endpointA1:endpointB1 job key
        await cursorRepo.SetCursorAsync("contacts:endpointA1:endpointB1", "endpointA1", string.Empty, "cursor-a");
        await cursorRepo.SetCursorAsync("contacts:endpointA1:endpointB1", "endpointB1", string.Empty, "cursor-b");
        // Seed a cursor for a different sync that must be unaffected
        await cursorRepo.SetCursorAsync("contacts:endpointA2:endpointB2", "endpointA2", string.Empty, "cursor-c");

        await host.ResetContactsSyncAsync("endpointA1", "endpointB1");

        Assert.Null(await cursorRepo.GetCursorAsync("contacts:endpointA1:endpointB1", "endpointA1"));
        Assert.Null(await cursorRepo.GetCursorAsync("contacts:endpointA1:endpointB1", "endpointB1"));
        Assert.NotNull(await cursorRepo.GetCursorAsync("contacts:endpointA2:endpointB2", "endpointA2"));
    }

    [Fact]
    public async Task ResetAllAsync_ClearsAllLinkStateAndCursors()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();
        var host = CreateHost(sourceA, targetA, sourceB, targetB);

        var cursorRepo = new EndpointCursorRepository(db);
        await cursorRepo.SetCursorAsync("contacts:endpointA1:endpointB1", "endpointA1", string.Empty, "cursor-a");
        await cursorRepo.SetCursorAsync("contacts:endpointA2:endpointB2", "endpointA2", string.Empty, "cursor-b");

        await host.ResetAllAsync();

        Assert.Null(await cursorRepo.GetCursorAsync("contacts:endpointA1:endpointB1", "endpointA1"));
        Assert.Null(await cursorRepo.GetCursorAsync("contacts:endpointA2:endpointB2", "endpointA2"));
    }

    [Fact]
    public async Task UnlockContactsSyncAsync_ReleasesSpecificLease()
    {
        FakeConnector sourceA = new();
        FakeConnector targetA = new();
        FakeConnector sourceB = new();
        FakeConnector targetB = new();
        var host = CreateHost(sourceA, targetA, sourceB, targetB);

        var leaseRepo = new LeaseRepository(db);
        await leaseRepo.TryAcquireAsync("contacts:endpointA1:endpointB1", "holder1", TimeSpan.FromHours(1));
        await leaseRepo.TryAcquireAsync("contacts:endpointA2:endpointB2", "holder2", TimeSpan.FromHours(1));

        int cleared = await host.UnlockContactsSyncAsync("endpointA1", "endpointB1");

        Assert.Equal(1, cleared);
        Assert.False(await leaseRepo.IsLockedAsync("contacts:endpointA1:endpointB1"));
        Assert.True(await leaseRepo.IsLockedAsync("contacts:endpointA2:endpointB2"));
    }

    private SyncHost CreateHost(
        FakeConnector sourceA,
        FakeConnector targetA,
        FakeConnector sourceB,
        FakeConnector targetB)
    {
        var connectors = new Dictionary<string, IConnector<CanonicalContact>>(StringComparer.OrdinalIgnoreCase)
        {
            ["endpointA1"] = sourceA,
            ["endpointB1"] = targetA,
            ["endpointA2"] = sourceB,
            ["endpointB2"] = targetB,
        };

        var provider = new ServiceCollection()
            .AddSingleton<Func<string, IConnector<CanonicalContact>>>(_ => name => connectors[name])
            .BuildServiceProvider();

        KagamiOptions options = new()
        {
            Host = new KagamiHostOptions
            {
                MaxConcurrentJobs = 1,
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
                ["job-a"] = CreateJob("endpointA1", "endpointB1"),
                ["job-b"] = CreateJob("endpointA2", "endpointB2"),
            },
        };

        JobExecutor executor = new(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new LeaseRepository(db),
            new SyncActionExecutor(new LinkStateRepository(db), new OperationLogRepository(db), NullLogger<SyncActionExecutor>.Instance),
            NullLogger<JobExecutor>.Instance);

        return new SyncHost(
            options,
            provider,
            executor,
            db,
            NullLogger<SyncHost>.Instance);
    }

    private static JobOptions CreateJob(string endpointA, string endpointB) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            SourceEndpointName = endpointA,
            DestinationEndpointName = endpointB,
            SyncMode = SyncMode.Bidirectional,
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
