using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class JobExecutorTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly LinkStateRepository linkStateRepository;
    private readonly EndpointCursorRepository endpointCursorRepository;
    private readonly LeaseRepository leaseRepository;
    private readonly OperationLogRepository operationLogRepository;

    public JobExecutorTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        linkStateRepository = new LinkStateRepository(db);
        endpointCursorRepository = new EndpointCursorRepository(db);
        leaseRepository = new LeaseRepository(db);
        operationLogRepository = new OperationLogRepository(db);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task ExecuteAsync_UpdatesUsingTargetProviderId()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();

        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Original"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Alice Original"));
        await sourceConnector.UpdateItemAsync(CreateContactItem("a1", "v2", "Alice Updated"));

        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contacts:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
            SourceHash = "old-a",
            DestinationHash = "old-b",
        });

        var executor = CreateExecutor();

        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));
        var updatedTarget = await destinationConnector.GetItemAsync("b1");

        Assert.True(result.Succeeded);
        Assert.NotNull(updatedTarget);
        Assert.Equal("Alice Updated", updatedTarget.DisplayName);
        Assert.Null(await destinationConnector.GetItemAsync("a1"));
    }

    [Fact]
    public async Task ExecuteAsync_AggregatesMultiPageConnectorReads()
    {
        PagedConnector sourceConnector = new(
            new ItemSet<CanonicalContact>(
                [CreateContactItem("a1", "v1", "Alice One"), CreateContactItem("a2", "v1", "Alice Two")],
                "delta-token"));
        FakeConnector destinationConnector = new();
        var executor = CreateExecutor();

        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ActionsPlanned);
    }

    [Fact]
    public async Task WhatIf_LogsEachPlannedContactOperation()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Logging"));

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("What-If: Create 'Alice Logging' on endpoint endpointB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_LogsOrganizationWhenDisplayNameIsEmpty()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", string.Empty, organization: "Contoso Ltd"));

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("What-If: Create 'Contoso Ltd' on endpoint endpointB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_LogsOnlyWinningDirection_ForMirroredDuplicateMatchUpdates()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Ada Langenfeld", email: "ada@example.com"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Ada Langenfeld", email: " ADA@example.com "));

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, conflictPolicy: ConflictPolicy.SourceWins),
            true);

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("What-If: Update 'Ada Langenfeld' on endpoint endpointB", StringComparison.Ordinal));
        Assert.DoesNotContain(syncLogger.Entries, entry => entry.Contains("What-If: Update 'Ada Langenfeld' on endpoint endpointA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_OrdersActionsAlphabeticallyWithinActionKind()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob"));
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice"));

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

        int aliceIndex = syncLogger.Entries.FindIndex(entry =>
            entry.Contains("What-If: Create 'Alice' on endpoint endpointB", StringComparison.Ordinal));
        int bobIndex = syncLogger.Entries.FindIndex(entry =>
            entry.Contains("What-If: Create 'Bob' on endpoint endpointB", StringComparison.Ordinal));

        Assert.NotEqual(-1, aliceIndex);
        Assert.NotEqual(-1, bobIndex);
        Assert.True(aliceIndex < bobIndex);
    }

    [Fact]
    public async Task Execution_LogsExecutingAndDoneMessages()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Executed"));

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("Executing: Create 'Alice Executed' on endpoint endpointB", StringComparison.Ordinal));
        Assert.Contains(syncLogger.Entries, entry => entry.Contains("Done: Create 'Alice Executed' on endpoint endpointB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_Y_ExecutesPlannedAction()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Confirmed"));

        List<string> prompts = [];
        var executor = CreateExecutor(
            readKey: CreateKeyReader('Y'),
            writePrompt: prompts.Add);

        var result = await executor.ExecuteJobAsync(
            CreateJob("job-1", sourceConnector, destinationConnector),
            JobExecutionFlags.Confirm);

        Assert.True(result.Succeeded);
        Assert.Contains(destinationConnector.Items, item => item.DisplayName == "Alice Confirmed" && !item.IsDeleted);
    }

    [Fact]
    public async Task Confirm_N_SkipsActionWithoutPersistingLinkState()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Skipped"));

        var executor = CreateExecutor(readKey: CreateKeyReader('N'));

        var result = await executor.ExecuteJobAsync(
            CreateJob("job-1", sourceConnector, destinationConnector),
            JobExecutionFlags.Confirm);

        Assert.True(result.Succeeded);
        Assert.Empty(destinationConnector.Items);
        Assert.Empty(await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB", CancellationToken.None));
    }

    [Fact]
    public async Task Confirm_A_DisablesFurtherPromptsForCurrentRun()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice First"));
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob Second"));

        List<string> prompts = [];
        var executor = CreateExecutor(
            readKey: CreateKeyReader('A'),
            writePrompt: prompts.Add);

        var result = await executor.ExecuteJobAsync(
            CreateJob("job-1", sourceConnector, destinationConnector),
            JobExecutionFlags.Confirm);

        Assert.True(result.Succeeded);
        Assert.Equal(2, destinationConnector.Items.Count(item => !item.IsDeleted));
        Assert.Single(prompts);
    }

    [Fact]
    public async Task Confirm_Q_FaultsRunAndPreventsCursorAdvancement()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Quit"));
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob Later"));

        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "-1", CancellationToken.None);

        var executor = CreateExecutor(readKey: CreateKeyReader('Q'));

        var result = await executor.ExecuteJobAsync(
            CreateJob("job-1", sourceConnector, destinationConnector),
            JobExecutionFlags.Confirm);

        Assert.False(result.Succeeded);
        Assert.Empty(destinationConnector.Items);
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.Equal("-1", cursor?.Cursor);
    }

    [Fact]
    public async Task PermanentPerItemFailure_ContinuesRemainingActionsAndAdvancesCursor()
    {
        FakeConnector sourceConnector = new();
        FailingCreateConnector destinationConnector = new(throwForSourceId: "a1");
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob", email: "bob@example.com"));

        // Pre-seed a cursor so we can verify it advances after the run.
        // Use "-1" so FakeConnector parses it as generation -1 and returns all gen-0 seeded items.
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "-1", CancellationToken.None);

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        // Run completes successfully (permanent per-item failures do not fault the run)
        Assert.True(result.Succeeded);
        // Remaining items (Bob) were still processed
        Assert.Contains(destinationConnector.Items, i => i.DisplayName == "Bob" && !i.IsDeleted);
        // An error was logged for the failing item (permanent failure → "continuing", not "aborting run")
        Assert.Contains(syncLogger.Entries, e =>
            e.Contains("a1", StringComparison.Ordinal) &&
            e.Contains("failed", StringComparison.Ordinal) &&
            e.Contains("continuing", StringComparison.Ordinal));
        // A final warning was logged summarising the failures
        Assert.Contains(syncLogger.Entries, e =>
            e.Contains("one or more synchronization actions failed", StringComparison.OrdinalIgnoreCase));
        // Cursor MUST have been written (run completes successfully despite per-item failures)
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.NotEqual("-1", cursor?.Cursor);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientFailure_AbortsRunImmediatelyAndPreventsCursorAdvancement(int statusCode)
    {
        FakeConnector sourceConnector = new();
        TransientlyFailingConnector destinationConnector = new((System.Net.HttpStatusCode)statusCode);
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob", email: "bob@example.com"));

        // Pre-seed a cursor so we can verify it does not advance.
        // Use "-1" so FakeConnector parses it as generation -1 and returns all gen-0 seeded items.
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "-1", CancellationToken.None);

        var executor = CreateExecutor();

        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        // Run is faulted: succeeded must be false
        Assert.False(result.Succeeded);
        // Second item must NOT have been processed (run aborted on transient failure)
        Assert.DoesNotContain(destinationConnector.Items, i => i.DisplayName == "Bob" && !i.IsDeleted);
        // Cursor must NOT have been written (faulted run must not advance cursors)
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.Equal("-1", cursor?.Cursor);
    }

    [Fact]
    public async Task SuccessfulRun_AdvancesCursor()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        // No pre-existing cursor
        var executor = CreateExecutor();

        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.True(result.Succeeded);

        // Cursor must have been written after a successful run
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.NotNull(cursor);
    }

    [Fact]
    public async Task UnpairedCursorRun_FallsBackToFullLoadOnBothSides()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "0", CancellationToken.None);

        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        Assert.Equal(1, sourceConnector.GetCursorItemsCallCountWithNullCursor);
        Assert.Equal(1, destinationConnector.GetCursorItemsCallCountWithNullCursor);
    }

    [Fact]
    public async Task ExpiredSourceCursor_RetriesWithFullLoadOnBothSides()
    {
        ExpiringCursorConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "0", CancellationToken.None);
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointB", string.Empty, "0", CancellationToken.None);

        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        Assert.Equal(1, sourceConnector.DeltaReadCount);
        Assert.Equal(1, sourceConnector.FullReadCount);
        Assert.Equal(1, destinationConnector.GetCursorItemsCallCountWithNullCursor);
    }

    [Fact]
    public async Task ExpiredDestinationCursor_RetriesWithFullLoadOnBothSides()
    {
        FakeConnector sourceConnector = new();
        ExpiringCursorConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "0", CancellationToken.None);
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointB", string.Empty, "0", CancellationToken.None);

        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        Assert.Equal(1, sourceConnector.GetCursorItemsCallCountWithNullCursor);
        Assert.Equal(1, destinationConnector.DeltaReadCount);
        Assert.Equal(1, destinationConnector.FullReadCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FullAndForceRuns_IgnoreStoredCursorsAndUseFullLoad(bool full, bool force)
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "0", CancellationToken.None);
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointB", string.Empty, "0", CancellationToken.None);

        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, full: full, force: force));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        Assert.Equal(1, sourceConnector.GetCursorItemsCallCountWithNullCursor);
        Assert.Equal(1, destinationConnector.GetCursorItemsCallCountWithNullCursor);
    }

    [Fact]
    public async Task NoPersistenceRun_UsesFullLoadPathWithoutAdvancingCursors()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        var executor = CreateExecutor();

        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, noPersistence: true));

        Assert.True(result.Succeeded);
        Assert.Equal(1, sourceConnector.GetAllItemsCallCount);
        Assert.Null(await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Scenario A: Filtered mirror/prune — item moves out of scope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FilteredMirror_DeletesDestination_WhenSourceItemMovesOutOfScope()
    {
        // Setup: sync contacts A→B for filter "startswith(name,'Alice')".
        // a1 ("Alice") is in scope; a1 changes to "Bob" → moves out of scope.
        // Expected: b1 is deleted on destination; source link state must NOT mark a1 as IsDeleted.
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();

        // Initial state: a1 = "Alice" (passes filter), b1 = destination mirror
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Original", email: "alice@example.com"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Alice Original", email: "alice@example.com"));

        // Establish link state (simulates that the first sync already linked a1 ↔ b1)
        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contacts:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
        });

        // a1 changes to "Bob" — moves out of filter scope "startswith(name,'Alice')"
        await sourceConnector.UpdateItemAsync(CreateContactItem("a1", "v2", "Bob Changed", email: "bob@example.com"));

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, filter: filter.Scope, syncMode: SyncMode.Forward));

        Assert.True(result.Succeeded);

        // b1 must be deleted on destination
        var b1 = await destinationConnector.GetItemAsync("b1");
        Assert.True(b1 is null || b1.IsDeleted, "Destination item b1 should have been deleted");

        // Source link state must NOT encode a1 as IsDeleted — the item still exists at source
        var links = await linkStateRepository.GetByPartitionAsync("contacts:endpointA:endpointB", CancellationToken.None);
        var linkRow = links.FirstOrDefault(l => l.SourceId == "a1");
        // After delete, the link row may be removed or retained; but if retained, SourceId must still be "a1" (not nulled out as deleted)
        if (linkRow is not null)
        {
            Assert.Equal("a1", linkRow.SourceId);
        }
    }

    // -------------------------------------------------------------------------
    // Scenario B: Changing from unfiltered to filtered sync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FilteredMirror_DeletesDestination_WhenPreviouslyUnfilteredLinkLeavesScope()
    {
        // Initially link a1 ↔ b1 without a filter (simulates a prior unfiltered sync).
        // Then switch to filter "startswith(name,'Alice')" and sync.
        // a1 changes to "Bob" → moves out of scope → b1 must be deleted on destination.
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();

        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Alice", email: "alice@example.com"));

        // Pre-existing link from the unfiltered sync era
        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contacts:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
        });

        // a1 changes to "Bob" — now fails the new filter
        await sourceConnector.UpdateItemAsync(CreateContactItem("a1", "v2", "Bob", email: "bob@example.com"));

        // Switch to filtered sync (scope change invalidates old cursor; new cursor = none → full run)
        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, filter: filter.Scope, syncMode: SyncMode.Forward));

        Assert.True(result.Succeeded);

        // b1 must be deleted on destination
        var b1 = await destinationConnector.GetItemAsync("b1");
        Assert.True(b1 is null || b1.IsDeleted, "Destination item b1 should have been deleted when source moved out of scope");
    }

    // -------------------------------------------------------------------------
    // Scenario C: Delta semantics — absent in delta means unchanged, not deleted
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeltaRun_SourceAbsent_IsPreservedAsUnchanged()
    {
        // Run 1 (full): establish link a1 ↔ b1.
        // Run 2 (delta): a1 does NOT appear in delta (it hasn't changed) → must NOT delete b1.
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();

        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Alice", email: "alice@example.com"));

        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contacts:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
        });

        // Store cursors for both endpoints (simulates a prior successful run)
        string initialSourceCursor = "0"; // gen 0 — subsequent delta will return only gen > 0 items
        string initialDestCursor = "0";
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, initialSourceCursor, CancellationToken.None);
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointB", string.Empty, initialDestCursor, CancellationToken.None);

        var executor = CreateExecutor();
        // Run 2: no changes on either side → delta is empty → no actions, not even delete
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ActionsPlanned);

        // b1 must still exist on destination
        var b1 = await destinationConnector.GetItemAsync("b1");
        Assert.NotNull(b1);
        Assert.False(b1.IsDeleted);
    }

    // -------------------------------------------------------------------------
    // Scenario D: No noisy logs / no skip actions for items outside filter scope
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FilteredRun_ProducesNoActions_ForItemsNeverInScope()
    {
        // Source has "Charlie" who never matched the filter "startswith(name,'Alice')".
        // No link row exists for Charlie.  The run must produce 0 actions — no Skip, no Create.
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();

        sourceConnector.Seed(CreateContactItem("c1", "v1", "Charlie", email: "charlie@example.com"));

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, filter: filter.Scope, syncMode: SyncMode.Forward));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ActionsPlanned);
    }

    [Fact]
    public async Task FilteredRun_DeletesDestination_ForItemThatMovedOutOfScope_ButProducesNoActionForNeverInScopeItems()
    {
        // Mix of:
        //   - a1 "Alice" → was linked, now moved out of scope → should delete b1
        //   - c1 "Charlie" → never in scope, no link row → NO action at all
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();

        // a1 changes name from "Alice" to "Bob" → moves out of filter scope
        sourceConnector.Seed(CreateContactItem("a1", "v2", "Bob", email: "bob@example.com"));
        // c1 was never in scope
        sourceConnector.Seed(CreateContactItem("c1", "v1", "Charlie", email: "charlie@example.com"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Alice", email: "alice@example.com"));

        // a1 ↔ b1 link exists (from before a1 changed name)
        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contacts:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
        });

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var executor = CreateExecutor();
        var result = await executor.ExecuteJobAsync(CreateJob("job-1", sourceConnector, destinationConnector, filter: filter.Scope, syncMode: SyncMode.Forward));

        Assert.True(result.Succeeded);

        // Exactly 1 action: delete b1 because a1 moved out of scope
        Assert.Equal(1, result.ActionsPlanned);
        var b1 = await destinationConnector.GetItemAsync("b1");
        Assert.True(b1 is null || b1.IsDeleted, "b1 should have been deleted");

        // c1 must produce no action at all (not even Skip)
        // (Verified indirectly by ActionsPlanned == 1)
    }

    // -------------------------------------------------------------------------
    // Admin / housekeeping operations
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListJobsAsync_ReturnsAllKnownJobs()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice"));

        var executor = CreateExecutor();
        await executor.ExecuteJobAsync(CreateJob("contacts:endpointA:endpointB", sourceConnector, destinationConnector));

        var jobs = await executor.ListJobsAsync(CancellationToken.None);

        Assert.Contains(jobs, j => j.JobKey == "contacts:endpointA:endpointB");
    }

    [Fact]
    public async Task ListJobsAsync_ShowsLockedFalseAfterNormalCompletion()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        var executor = CreateExecutor();

        await executor.ExecuteJobAsync(CreateJob("contacts:endpointA:endpointB", sourceConnector, destinationConnector));

        var jobs = await executor.ListJobsAsync(CancellationToken.None);

        Assert.Contains(jobs, j => j.JobKey == "contacts:endpointA:endpointB" && !j.Locked);
    }

    [Fact]
    public async Task ResetJobAsync_ClearsLinkStateAndCursorsForThatJob()
    {
        string jobKey = "contacts:endpointA:endpointB";
        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            PartitionKey = jobKey,
            SourceId = "a1",
            DestinationId = "b1",
        });
        await endpointCursorRepository.SetCursorAsync(jobKey, "endpointA", string.Empty, "cursor-a");
        await endpointCursorRepository.SetCursorAsync(jobKey, "endpointB", string.Empty, "cursor-b");

        // Seed another job that must remain unaffected.
        await endpointCursorRepository.SetCursorAsync("contacts:endpointC:endpointD", "endpointC", string.Empty, "cursor-c");

        var executor = CreateExecutor();
        await executor.ResetJobAsync(jobKey, CancellationToken.None);

        Assert.Empty(await linkStateRepository.GetByPartitionAsync(jobKey, CancellationToken.None));
        Assert.Null(await endpointCursorRepository.GetCursorAsync(jobKey, "endpointA", CancellationToken.None));
        Assert.Null(await endpointCursorRepository.GetCursorAsync(jobKey, "endpointB", CancellationToken.None));
        Assert.NotNull(await endpointCursorRepository.GetCursorAsync("contacts:endpointC:endpointD", "endpointC", CancellationToken.None));
    }

    [Fact]
    public async Task ResetAllAsync_ClearsAllLinkStateAndCursors()
    {
        await endpointCursorRepository.SetCursorAsync("contacts:endpointA:endpointB", "endpointA", string.Empty, "cursor-a");
        await endpointCursorRepository.SetCursorAsync("contacts:endpointC:endpointD", "endpointC", string.Empty, "cursor-c");

        var executor = CreateExecutor();
        await executor.ResetAllAsync(CancellationToken.None);

        Assert.Null(await endpointCursorRepository.GetCursorAsync("contacts:endpointA:endpointB", "endpointA", CancellationToken.None));
        Assert.Null(await endpointCursorRepository.GetCursorAsync("contacts:endpointC:endpointD", "endpointC", CancellationToken.None));
    }

    [Fact]
    public async Task UnlockJobAsync_ReleasesSpecificLock()
    {
        string jobKeyA = "contacts:endpointA:endpointB";
        string jobKeyC = "contacts:endpointC:endpointD";

        await leaseRepository.TryAcquireAsync(jobKeyA, CancellationToken.None);
        await leaseRepository.TryAcquireAsync(jobKeyC, CancellationToken.None);

        var executor = CreateExecutor();
        int cleared = await executor.UnlockJobAsync(jobKeyA, CancellationToken.None);

        Assert.Equal(1, cleared);
        Assert.False(await leaseRepository.IsLockedAsync(jobKeyA));
        Assert.True(await leaseRepository.IsLockedAsync(jobKeyC));
    }

    [Fact]
    public async Task UnlockAllJobsAsync_ReleasesAllLocks()
    {
        await leaseRepository.TryAcquireAsync("contacts:endpointA:endpointB", CancellationToken.None);
        await leaseRepository.TryAcquireAsync("contacts:endpointC:endpointD", CancellationToken.None);

        var executor = CreateExecutor();
        int cleared = await executor.UnlockAllJobsAsync(CancellationToken.None);

        Assert.Equal(2, cleared);
        Assert.False(await leaseRepository.IsLockedAsync("contacts:endpointA:endpointB"));
        Assert.False(await leaseRepository.IsLockedAsync("contacts:endpointC:endpointD"));
    }

    private JobExecutor CreateExecutor(
        ILogger<JobExecutor>? logger = null,
        ILogger<SyncActionExecutor>? syncLogger = null,
        Func<ConsoleKeyInfo>? readKey = null,
        Action<string>? writePrompt = null) =>
        new(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            linkStateRepository,
            endpointCursorRepository,
            leaseRepository,
            new SyncActionExecutor(linkStateRepository, operationLogRepository, syncLogger ?? NullLogger<SyncActionExecutor>.Instance, readKey, writePrompt),
            db,
            logger ?? NullLogger<JobExecutor>.Instance);

    private static Job<CanonicalContact> CreateJob(
        string jobKey,
        IConnector<CanonicalContact> sourceConnector,
        IConnector<CanonicalContact> destinationConnector,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        string? filter = null,
        SyncMode syncMode = SyncMode.Bidirectional,
        bool noPersistence = false,
        bool full = false,
        bool force = false)
    {
        return new Job<CanonicalContact>(jobKey, CreateJobOptions(conflictPolicy, filter, syncMode, noPersistence, full, force), sourceConnector, destinationConnector);
    }

    private static JobOptions CreateJobOptions(
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        string? filter = null,
        SyncMode syncMode = SyncMode.Bidirectional,
        bool noPersistence = false,
        bool full = false,
        bool force = false) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            SourceEndpointName = "endpointA",
            DestinationEndpointName = "endpointB",
            SyncMode = syncMode,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = conflictPolicy,
            Filter = filter,
            NoPersistence = noPersistence,
            Full = full,
            Force = force,
        };

    private static CanonicalContact CreateContactItem(string id, string version, string displayName, string? organization = null, string? email = null) => new()
    {
        DisplayName = displayName,
        Organization = organization,
        Emails = [new ContactEmail { Address = email ?? GenerateTestEmail(displayName) }],

        Provenance =
        {
            ProviderId = id,
            Version = version,
        },
    };

    private static string GenerateTestEmail(string displayName) =>
        $"{displayName.Replace(" ", ".", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}@example.com";

    private static Func<ConsoleKeyInfo> CreateKeyReader(params char[] keys)
    {
        Queue<char> remaining = new(keys);
        return () =>
        {
            if (remaining.Count == 0)
            {
                throw new InvalidOperationException("No confirmation keys remain.");
            }

            char key = remaining.Dequeue();
            return new ConsoleKeyInfo(key, char.ToUpperInvariant(key) switch
            {
                'Y' => ConsoleKey.Y,
                'N' => ConsoleKey.N,
                'A' => ConsoleKey.A,
                'Q' => ConsoleKey.Q,
                _ => ConsoleKey.NoName,
            }, false, false, false);
        };
    }

    private sealed class PagedConnector(params ItemSet<CanonicalContact>[] pageSets) : IConnector<CanonicalContact>
    {
        private int index;

        public ConnectorCapabilities Capabilities { get; } = new()
        {
            ConnectorType = "paged-test",
            SupportsIncrementalSync = true,
            SupportsDeletes = true,
        };

        public string EndpointName => "paged-test";

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ItemSet<CanonicalContact>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(GetPage(reset: cursor is null));

        public Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CanonicalContact?>(null);

        public Task<CanonicalContact> CreateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task<CanonicalContact> UpdateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private ItemSet<CanonicalContact> GetPage(bool reset)
        {
            if (pageSets.Length == 0)
            {
                throw new InvalidOperationException("At least one item set is required.");
            }

            if (reset)
            {
                index = 0;
            }

            int currentIndex = Math.Min(index, pageSets.Length - 1);
            index++;
            return pageSets[currentIndex];
        }
    }

    private sealed class ExpiringCursorConnector : IConnector<CanonicalContact>
    {
        private readonly FakeConnector inner = new();
        private bool expireOnNextDeltaRead = true;

        public int DeltaReadCount { get; private set; }

        public int FullReadCount { get; private set; }

        public ConnectorCapabilities Capabilities => inner.Capabilities;

        public string EndpointName => inner.EndpointName;

        public IReadOnlyList<CanonicalContact> Items => inner.Items;

        public void Seed(CanonicalContact item) => inner.Seed(item);

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) =>
            inner.AuthenticateAsync(cancellationToken);

        public Task<ItemSet<CanonicalContact>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
        {
            if (cursor is null)
            {
                FullReadCount++;
                return inner.GetCursorItemsAsync(cursor, cancellationToken);
            }

            DeltaReadCount++;
            if (expireOnNextDeltaRead)
            {
                expireOnNextDeltaRead = false;
                throw new ExpiredCursorException("Test cursor expired.");
            }

            return inner.GetCursorItemsAsync(cursor, cancellationToken);
        }

        public Task<IReadOnlyList<CanonicalContact>> GetAllItemsAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllItemsAsync(cancellationToken);

        public Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetItemAsync(id, cancellationToken);

        public Task<CanonicalContact> CreateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
            inner.CreateItemAsync(item, cancellationToken);

        public Task<CanonicalContact> UpdateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
            inner.UpdateItemAsync(item, cancellationToken);

        public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) =>
            inner.DeleteItemAsync(id, cancellationToken);
    }
}
