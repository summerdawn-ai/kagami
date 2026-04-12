namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

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
    public async Task ExecuteAsyncUpdatesUsingTargetProviderId()
    {
        FakeConnector connectorA = new();
        FakeConnector connectorB = new();

        connectorA.Seed(CreateContactItem("a1", "v1", "Alice Original"));
        connectorB.Seed(CreateContactItem("b1", "v1", "Alice Original"));
        await connectorA.UpdateItemAsync(CreateContactItem("a1", "v2", "Alice Updated"));

        await linkStateRepository.UpsertAsync(new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.Contact,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
            SideAHash = "old-a",
            SideBHash = "old-b",
        });

        JobExecutor executor = CreateExecutor();

        JobExecutionResult result = await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB);
        CanonicalItem? updatedTarget = await connectorB.GetItemAsync("b1");

        Assert.True(result.Succeeded);
        Assert.NotNull(updatedTarget);
        Assert.Equal("Alice Updated", ((CanonicalContact)updatedTarget.Payload!).DisplayName);
        Assert.Null(await connectorB.GetItemAsync("a1"));
    }

    [Fact]
    public async Task ExecuteAsyncAggregatesMultiPageConnectorReads()
    {
        PagedConnector connectorA = new(
            new IncrementalPage
            {
                Items = [CreateContactItem("a1", "v1", "Alice One")],
                HasMore = true,
                NextCursor = "page-2",
            },
            new IncrementalPage
            {
                Items = [CreateContactItem("a2", "v1", "Alice Two")],
                HasMore = false,
                NextCursor = "delta-token",
            });
        FakeConnector connectorB = new();
        JobExecutor executor = CreateExecutor();

        JobExecutionResult result = await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB, whatIf: true);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ActionsPlanned);
    }

    [Fact]
    public async Task WhatIfLogsEachPlannedContactOperation()
    {
        FakeConnector connectorA = new();
        FakeConnector connectorB = new();
        connectorA.Seed(CreateContactItem("a1", "v1", "Alice Logging"));

        InMemoryLogger<JobExecutor> logger = new();
        JobExecutor executor = CreateExecutor(logger);

        await executor.ExecuteAsync("job-1", CreateJob(), connectorA, connectorB, whatIf: true);

        Assert.Contains(logger.Entries, entry => entry.Contains("would create contact 'Alice Logging'", StringComparison.Ordinal));
    }

    private JobExecutor CreateExecutor(ILogger<JobExecutor>? logger = null) =>
        new(
            new Planner(NullLogger<Planner>.Instance),
            linkStateRepository,
            endpointCursorRepository,
            operationLogRepository,
            leaseRepository,
            logger ?? NullLogger<JobExecutor>.Instance);

    private static JobOptions CreateJob() =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            EndpointA = "endpointA",
            EndpointB = "endpointB",
            SyncMode = SyncMode.Bidirectional,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = ConflictPolicy.LastWriteWins,
        };

    private static CanonicalItem CreateContactItem(string id, string version, string displayName) =>
        new()
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Version = version,
            Payload = new CanonicalContact
            {
                DisplayName = displayName,
                Emails = [new ContactEmail { Address = GenerateTestEmail(displayName) }],
            },
        };

    private static string GenerateTestEmail(string displayName) =>
        $"{displayName.Replace(" ", ".", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}@example.com";

    private sealed class PagedConnector(params IncrementalPage[] pages) : IConnector
    {
        private readonly Queue<IncrementalPage> queuedPages = new(pages);

        public ConnectorCapabilities Capabilities { get; } = new()
        {
            ConnectorType = "paged-test",
            SupportsIncrementalSync = true,
            SupportsDeletes = true,
        };

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(queuedPages.Dequeue());

        public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(queuedPages.Dequeue());

        public Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CanonicalItem?>(null);

        public Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
