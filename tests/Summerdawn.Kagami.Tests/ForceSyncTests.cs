using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

using ContactFilter = Summerdawn.Kagami.Models.ContactFilter;

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
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            linkStateRepository,
            endpointCursorRepository,
            leaseRepository,
            new SyncActionExecutor(linkStateRepository, operationLogRepository, NullLogger<SyncActionExecutor>.Instance),
            NullLogger<JobExecutor>.Instance);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task ForceResync_WhenNothingChanged()
    {
        // Initial sync creates link state
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        // Verify link state created; destinationConnector now has the contact
        var linksAfterFirst = await linkStateRepository.GetByPartitionAsync("contact:endpointA:endpointB");
        Assert.Single(linksAfterFirst);
        int bItemsAfterFirst = destinationConnector.Items.Count;

        // Normal second sync is a no-op (item hasn't changed, cursor exists)
        var normalResult = await executor.ExecuteAsync(CreateJob("job-1"));
        Assert.True(normalResult.Succeeded);
        Assert.Equal(0, normalResult.ActionsPlanned);

        // Force sync should re-evaluate the item and plan an update
        var forceResult = await executor.ExecuteAsync(CreateJob("job-1", force: true));
        Assert.True(forceResult.Succeeded);
        Assert.True(forceResult.ActionsPlanned > 0, "Force sync should plan at least one action");

        // Item count in destinationConnector should not have duplicated (it's an update, not create)
        Assert.Equal(bItemsAfterFirst, destinationConnector.Items.Count(i => !i.IsDeleted));
    }

    [Fact]
    public async Task SecondRunWithCursorAtEnd_ProducesZeroActionsAndNoSkips()
    {
        // Seed one contact and run a first sync to establish cursors and link state.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        var firstResult = await executor.ExecuteAsync(CreateJob("job-1"));
        Assert.True(firstResult.Succeeded);
        Assert.Equal(1, firstResult.ActionsPlanned);

        // Record how many times GetInitialPageAsync was called during the first run.
        int initialPageCallsAfterFirstRun = sourceConnector.GetInitialPageCallCount;

        // Second run with no changes: the cursor is at the end of all items so the
        // incremental read returns an empty delta. The planner sees no items and produces
        // zero actions — including zero Skip actions.
        var secondResult = await executor.ExecuteAsync(CreateJob("job-1"));
        Assert.True(secondResult.Succeeded);
        Assert.Equal(0, secondResult.ActionsPlanned);

        // The source connector's initial-page endpoint must not have been called again;
        // only the incremental (delta) endpoint should be used on this run.
        Assert.Equal(initialPageCallsAfterFirstRun, sourceConnector.GetInitialPageCallCount);
    }

    [Fact]
    public async Task Force_WhatIf_LogsActionsWithoutWriting()
    {
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        var linksAfterSync = await linkStateRepository.GetByPartitionAsync("contact:endpointA:endpointB");
        var lastSyncedAt = linksAfterSync[0].LastSyncedAt;

        // force + whatIf: should plan actions but NOT write
        var result = await executor.ExecuteAsync(CreateJob("job-1", force: true), whatIf: true);
        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);

        // State must be unchanged
        var linksAfterWhatIf = await linkStateRepository.GetByPartitionAsync("contact:endpointA:endpointB");
        Assert.Equal(lastSyncedAt, linksAfterWhatIf[0].LastSyncedAt);
    }

    [Fact]
    public async Task Filter_RestrictsScope()
    {
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));

        var filter = ContactFilter.Parse("startswith(name,'A')")!;
        var result = await executor.ExecuteAsync(CreateJob("job-1") with { Filter = filter });

        Assert.True(result.Succeeded);

        // Only Alice should have been synced to destinationConnector
        var syncedContacts = destinationConnector.Items
            .Where(i => !i.IsDeleted)
            .ToList();

        Assert.Single(syncedContacts);
        Assert.Equal("Alice", syncedContacts[0]!.DisplayName);
    }

    [Fact]
    public async Task FilteredSync_LeavesOutOfScopeContactsUntouched()
    {
        // Pre-seed destinationConnector with Bob (simulate Bob already existing there)
        destinationConnector.Seed(CreateContact("b-bob", "Bob"));

        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));

        var filter = ContactFilter.Parse("startswith(name,'A')")!;
        await executor.ExecuteAsync(CreateJob("job-1") with { Filter = filter });

        // Bob was on B before the sync; he must still be there
        Assert.Contains(destinationConnector.Items, i => i.Provenance.ProviderId == "b-bob" && !i.IsDeleted);
    }

    [Fact]
    public async Task ChangingFilterScope_ForcesFullEnumeration()
    {
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));

        var filteredScope = ContactFilter.Parse("startswith(name,'A')")!;
        var filteredResult = await executor.ExecuteAsync(CreateJob("job-1") with { Filter = filteredScope });
        var filteredCursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA");

        var unfilteredResult = await executor.ExecuteAsync(CreateJob("job-1"));
        var unfilteredCursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA");

        Assert.True(filteredResult.Succeeded);
        Assert.NotNull(filteredCursor);
        Assert.Equal("startswith(name,'A')", filteredCursor!.Scope);
        Assert.True(unfilteredResult.Succeeded);
        Assert.Equal(1, unfilteredResult.ActionsPlanned);
        Assert.Equal(2, destinationConnector.Items.Count(item => !item.IsDeleted));
        Assert.Contains(destinationConnector.Items, item => item is { DisplayName: "Bob", IsDeleted: false });
        Assert.NotNull(unfilteredCursor);
        Assert.Equal(string.Empty, unfilteredCursor!.Scope);
    }

    [Fact]
    public async Task FullSync_SkipsContactsWhoseContentIsAlreadySynced()
    {
        // Initial sync establishes link state and cursors.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        var linksAfterFirst = await linkStateRepository.GetByPartitionAsync("contact:endpointA:endpointB");
        Assert.Single(linksAfterFirst);

        // --full re-fetches everything (ignores cursor) but still runs HasChanged.
        // Because nothing has changed since the last sync, HasChanged returns false and
        // the planner emits no Update actions — so ActionsPlanned must be 0.
        var fullResult = await executor.ExecuteAsync(CreateJob("job-1", full: true));
        Assert.True(fullResult.Succeeded);
        Assert.Equal(0, fullResult.ActionsPlanned);
    }

    [Fact]
    public async Task FullSync_WhatIf_DoesNotShowFalseUpdates()
    {
        // Initial sync establishes link state.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        sourceConnector.Seed(CreateContact("a2", "Bob"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        // --full --what-if must not report any updates when nothing has changed.
        var whatIfResult = await executor.ExecuteAsync(CreateJob("job-1", full: true), whatIf: true);
        Assert.True(whatIfResult.Succeeded);
        Assert.Equal(0, whatIfResult.ActionsPlanned);
    }

    /// <summary>
    /// When only the remote version changes (same canonical content), a normal sync produces no
    /// write actions — the item is recognised as content-unchanged and a Skip is emitted.
    /// The link state is refreshed with the new version so the next run does not re-evaluate.
    /// </summary>
    [Fact]
    public async Task VersionBumpWithSameContent_WithoutForce_ProducesNoActions()
    {
        // Establish link state for "Alice" via an initial sync.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        var linksAfterFirst = await linkStateRepository.GetByPartitionAsync("contact:endpointA:endpointB");
        Assert.Single(linksAfterFirst);

        // Bump the source version without changing any canonical content fields.
        await sourceConnector.UpdateItemAsync(CreateContact("a1", "Alice"));

        // A normal sync should detect the version change but conclude the content is
        // identical and emit no write actions.
        var result = await executor.ExecuteAsync(CreateJob("job-1"));
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ActionsPlanned);

        // Link state must be refreshed with the new source version.
        var linksAfterSecond = await linkStateRepository.GetByPartitionAsync("contact:endpointA:endpointB");
        Assert.Single(linksAfterSecond);
        Assert.NotEqual(linksAfterFirst[0].SourceVersion, linksAfterSecond[0].SourceVersion);
    }

    /// <summary>
    /// With <c>--force</c>, even a version-only change (identical canonical content) produces
    /// an unconditional Update action — the content-sameness check is bypassed.
    /// </summary>
    [Fact]
    public async Task VersionBumpWithSameContent_WithForce_ProducesUpdate()
    {
        // Establish link state for "Alice" via an initial sync.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        await executor.ExecuteAsync(CreateJob("job-1"));

        // Bump the source version without changing any canonical content fields.
        await sourceConnector.UpdateItemAsync(CreateContact("a1", "Alice"));

        // A forced sync must produce at least one write action even though content is identical.
        var forceResult = await executor.ExecuteAsync(CreateJob("job-1", force: true));
        Assert.True(forceResult.Succeeded);
        Assert.True(forceResult.ActionsPlanned > 0, "--force should produce at least one update action");
    }

    /// <summary>
    /// An inferred link (matched by name, no prior link row) whose content is already identical
    /// on both sides produces no write actions on a normal sync.
    /// </summary>
    [Fact]
    public async Task InferredLink_IdenticalContent_WithoutForce_ProducesNoActions()
    {
        // Seed the same contact on both sides with identical content but different provider IDs.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        destinationConnector.Seed(CreateContact("b1", "Alice"));

        // No link state exists — the planner will infer a link by name matching.
        var result = await executor.ExecuteAsync(CreateJob("job-1"));
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ActionsPlanned);
    }

    /// <summary>
    /// With <c>--force</c>, an inferred link whose content is already identical on both sides
    /// still produces an Update action — the content-sameness guard is bypassed.
    /// </summary>
    [Fact]
    public async Task InferredLink_IdenticalContent_WithForce_ProducesUpdate()
    {
        // Seed the same contact on both sides with identical content but different provider IDs.
        sourceConnector.Seed(CreateContact("a1", "Alice"));
        destinationConnector.Seed(CreateContact("b1", "Alice"));

        // No link state exists. With --force the content-equality guard must be bypassed
        // and an unconditional write action must be planned.
        var forceResult = await executor.ExecuteAsync(CreateJob("job-1", force: true));
        Assert.True(forceResult.Succeeded);
        Assert.True(forceResult.ActionsPlanned > 0, "--force on an inferred link should produce at least one update action");
    }

    private Job<CanonicalContact> CreateJob(string jobKey, bool force = false, bool full = false) =>
        new(jobKey, CreateJobOptions(force, full), sourceConnector, destinationConnector);

    private static JobOptions CreateJobOptions(bool force = false, bool full = false) => new()
    {
        Enabled = true,
        EntityType = EntityType.Contact,
        Source = "endpointA",
        Destination = "endpointB",
        SyncMode = SyncMode.Bidirectional,
        DeletePolicy = DeletePolicy.Mirror,
        ConflictPolicy = ConflictPolicy.LastWriteWins,
        Force = force,
        Full = full,
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
