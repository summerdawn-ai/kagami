
using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;
public sealed class ForceSyncTests : IDisposable
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

    public ForceSyncTests()
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
    public async Task ForceResyncWhenNothingChanged()
    {
        // Initial sync creates link state
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);

        // Verify link state created; destinationConnector now has the contact
        var linksAfterFirst = await linkStateRepository.GetByJobAsync("job-1");
        Assert.Single(linksAfterFirst);
        int bItemsAfterFirst = destinationConnector.Items.Count;

        // Normal second sync is a no-op (item hasn't changed, cursor exists)
        var normalResult = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);
        Assert.True(normalResult.Succeeded);
        Assert.Equal(0, normalResult.ActionsPlanned);

        // Force sync should re-evaluate the item and plan an update
        var forceResult = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, force: true);
        Assert.True(forceResult.Succeeded);
        Assert.True(forceResult.ActionsPlanned > 0, "Force sync should plan at least one action");

        // Item count in destinationConnector should not have duplicated (it's an update, not create)
        Assert.Equal(bItemsAfterFirst, destinationConnector.Items.Count(i => !i.IsDeleted));
    }

    [Fact]
    public async Task ForceWhatIfLogsActionsWithoutWriting()
    {
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);

        var linksAfterSync = await linkStateRepository.GetByJobAsync("job-1");
        var lastSyncedAt = linksAfterSync[0].LastSyncedAt;

        // force + whatIf: should plan actions but NOT write
        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, whatIf: true, force: true);
        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);

        // State must be unchanged
        var linksAfterWhatIf = await linkStateRepository.GetByJobAsync("job-1");
        Assert.Equal(lastSyncedAt, linksAfterWhatIf[0].LastSyncedAt);
    }

    [Fact]
    public async Task FilterRestrictsScope()
    {
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));

        var filter = ContactFilter.Parse("startswith(name,'A')")!;
        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, filter: filter);

        Assert.True(result.Succeeded);

        // Only Alice should have been synced to destinationConnector
        var syncedContacts = destinationConnector.Items
            .Where(i => !i.IsDeleted)
            .Select(i => i.Payload as CanonicalContact)
            .Where(c => c is not null)
            .ToList();

        Assert.Single(syncedContacts);
        Assert.Equal("Alice", syncedContacts[0]!.DisplayName);
    }

    [Fact]
    public async Task FilteredSyncLeavesOutOfScopeContactsUntouched()
    {
        // Pre-seed destinationConnector with Bob (simulate Bob already existing there)
        destinationConnector.Seed(CreateContact("b-bob", "Bob"));

        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));

        var filter = ContactFilter.Parse("startswith(name,'A')")!;
        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, filter: filter);

        // Bob was on B before the sync; he must still be there
        Assert.Contains(destinationConnector.Items, i => i.SourceId == "b-bob" && !i.IsDeleted);
    }

    [Fact]
    public async Task ChangingFilterScopeForcesFullEnumeration()
    {
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));

        var filteredScope = ContactFilter.Parse("startswith(name,'A')")!;
        var filteredResult = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, filter: filteredScope);
        var filteredCursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA");

        var unfilteredResult = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);
        var unfilteredCursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA");

        Assert.True(filteredResult.Succeeded);
        Assert.NotNull(filteredCursor);
        Assert.Equal("startswith(name,'A')", filteredCursor!.Scope);
        Assert.True(unfilteredResult.Succeeded);
        Assert.Equal(1, unfilteredResult.ActionsPlanned);
        Assert.Equal(2, destinationConnector.Items.Count(item => !item.IsDeleted));
        Assert.Contains(destinationConnector.Items, item => item.Payload is CanonicalContact { DisplayName: "Bob" } && !item.IsDeleted);
        Assert.NotNull(unfilteredCursor);
        Assert.Equal(string.Empty, unfilteredCursor!.Scope);
    }

    private static JobOptions CreateJob() =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = "endpointA",
            Destination = "endpointB",
            SyncMode = SyncMode.Bidirectional,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = ConflictPolicy.LastWriteWins,
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
