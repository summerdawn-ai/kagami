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
            JobKey = "job-1",
            EntityType = EntityType.Contact,
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
            SourceHash = "old-a",
            DestinationHash = "old-b",
        });

        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector));
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
            new IncrementalPage<CanonicalContact>
            {
                Items = [CreateContactItem("a1", "v1", "Alice One")],
                HasMore = true,
                NextCursor = "page-2",
            },
            new IncrementalPage<CanonicalContact>
            {
                Items = [CreateContactItem("a2", "v1", "Alice Two")],
                HasMore = false,
                NextCursor = "delta-token",
            });
        FakeConnector destinationConnector = new();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

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

        await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("would create contact 'Alice Logging'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_LogsOrganizationWhenDisplayNameIsEmpty()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", string.Empty, organization: "Contoso Ltd"));

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector), whatIf: true);

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("would create contact 'Contoso Ltd'", StringComparison.Ordinal));
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

        await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector, conflictPolicy: ConflictPolicy.SourceWins),
            true);

        Assert.Contains(syncLogger.Entries, entry => entry.Contains("would update contact 'Ada Langenfeld' in direction SourceToDestination", StringComparison.Ordinal));
        Assert.DoesNotContain(syncLogger.Entries, entry => entry.Contains("would update contact 'Ada Langenfeld' in direction DestinationToSource", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermanentPerItemFailure_ContinuesRemainingActionsButPreventsCursorAdvancement()
    {
        FakeConnector sourceConnector = new();
        FailingCreateConnector destinationConnector = new(throwForSourceId: "a1");
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob", email: "bob@example.com"));

        // Pre-seed a cursor so we can verify it does not advance
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "old-cursor", CancellationToken.None);

        InMemoryLogger<SyncActionExecutor> syncLogger = new();
        var executor = CreateExecutor(syncLogger: syncLogger);

        var result = await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        // Run is faulted: succeeded must be false
        Assert.False(result.Succeeded);
        // Remaining items (Bob) were still processed
        Assert.Contains(destinationConnector.Items, i => i.DisplayName == "Bob" && !i.IsDeleted);
        // An error was logged for the failing item (permanent failure → "continuing", not "aborting run")
        Assert.Contains(syncLogger.Entries, e =>
            e.Contains("a1", StringComparison.Ordinal) &&
            e.Contains("failed", StringComparison.Ordinal) &&
            e.Contains("continuing", StringComparison.Ordinal));
        // Cursor must NOT have advanced
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.Equal("old-cursor", cursor?.Cursor);
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

        // Pre-seed a cursor so we can verify it does not advance
        await endpointCursorRepository.SetCursorAsync("job-1", "endpointA", string.Empty, "old-cursor", CancellationToken.None);

        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        // Run is faulted: succeeded must be false
        Assert.False(result.Succeeded);
        // Second item must NOT have been processed (run aborted on transient failure)
        Assert.DoesNotContain(destinationConnector.Items, i => i.DisplayName == "Bob" && !i.IsDeleted);
        // Cursor must NOT have advanced
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.Equal("old-cursor", cursor?.Cursor);
    }

    [Fact]
    public async Task SuccessfulRun_AdvancesCursor()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));

        // No pre-existing cursor
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(CreateJob("job-1", sourceConnector, destinationConnector));

        Assert.True(result.Succeeded);

        // Cursor must have been written after a successful run
        var cursor = await endpointCursorRepository.GetCursorAsync("job-1", "endpointA", CancellationToken.None);
        Assert.NotNull(cursor);
    }

    private JobExecutor CreateExecutor(ILogger<JobExecutor>? logger = null, ILogger<SyncActionExecutor>? syncLogger = null) =>
        new(
            new Planner(NullLogger<Planner>.Instance),
            linkStateRepository,
            endpointCursorRepository,
            leaseRepository,
            new SyncActionExecutor(linkStateRepository, operationLogRepository, syncLogger ?? NullLogger<SyncActionExecutor>.Instance),
            logger ?? NullLogger<JobExecutor>.Instance);

    private static Job<CanonicalContact> CreateJob(string jobKey, IConnector<CanonicalContact> sourceConnector, IConnector<CanonicalContact> destinationConnector, ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins)
    {
        return new Job<CanonicalContact>(jobKey, CreateJobOptions(conflictPolicy), sourceConnector, destinationConnector);
    }

    private static JobOptions CreateJobOptions(ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = "endpointA",
            Destination = "endpointB",
            SyncMode = SyncMode.Bidirectional,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = conflictPolicy,
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

    private sealed class PagedConnector(params IncrementalPage<CanonicalContact>[] pages) : IConnector<CanonicalContact>
    {
        private int index;

        public ConnectorCapabilities Capabilities { get; } = new()
        {
            ConnectorType = "paged-test",
            SupportsIncrementalSync = true,
            SupportsDeletes = true,
        };

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IncrementalPage<CanonicalContact>> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GetPage(reset: true));

        public Task<IncrementalPage<CanonicalContact>> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(GetPage());

        public Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CanonicalContact?>(null);

        public Task<CanonicalContact> CreateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task<CanonicalContact> UpdateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private IncrementalPage<CanonicalContact> GetPage(bool reset = false)
        {
            if (pages.Length == 0)
            {
                throw new InvalidOperationException("At least one page is required.");
            }

            if (reset)
            {
                index = 0;
            }

            int currentIndex = Math.Min(index, pages.Length - 1);
            index++;
            return pages[currentIndex];
        }
    }
}
