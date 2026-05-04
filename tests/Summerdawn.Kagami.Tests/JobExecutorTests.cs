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

        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);
        var updatedTarget = await destinationConnector.GetItemAsync("b1");

        Assert.True(result.Succeeded);
        Assert.NotNull(updatedTarget);
        Assert.Equal("Alice Updated", ((CanonicalContact)updatedTarget.Payload!).DisplayName);
        Assert.Null(await destinationConnector.GetItemAsync("a1"));
    }

    [Fact]
    public async Task ExecuteAsync_AggregatesMultiPageConnectorReads()
    {
        PagedConnector sourceConnector = new(
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
        FakeConnector destinationConnector = new();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, whatIf: true);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ActionsPlanned);
    }

    [Fact]
    public async Task WhatIf_LogsEachPlannedContactOperation()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice Logging"));

        InMemoryLogger<JobExecutor> logger = new();
        var executor = CreateExecutor(logger);

        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, whatIf: true);

        Assert.Contains(logger.Entries, entry => entry.Contains("would create contact 'Alice Logging'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_LogsOrganizationWhenDisplayNameIsEmpty()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", string.Empty, organization: "Contoso Ltd"));

        InMemoryLogger<JobExecutor> logger = new();
        var executor = CreateExecutor(logger);

        await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector, whatIf: true);

        Assert.Contains(logger.Entries, entry => entry.Contains("would create contact 'Contoso Ltd'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_LogsOnlyWinningDirection_ForMirroredDuplicateMatchUpdates()
    {
        FakeConnector sourceConnector = new();
        FakeConnector destinationConnector = new();
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Ada Langenfeld", email: "ada@example.com"));
        destinationConnector.Seed(CreateContactItem("b1", "v1", "Ada Langenfeld", email: " ADA@example.com "));

        InMemoryLogger<JobExecutor> logger = new();
        var executor = CreateExecutor(logger);

        await executor.ExecuteAsync(
            "job-1",
            CreateJob(conflictPolicy: ConflictPolicy.SourceWins),
            sourceConnector,
            destinationConnector,
            whatIf: true);

        Assert.Contains(logger.Entries, entry => entry.Contains("would update contact 'Ada Langenfeld' on side Destination", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Contains("would update contact 'Ada Langenfeld' on side Source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyActionsAsync_SingleContactFailure_ContinuesToNextContact()
    {
        FakeConnector sourceConnector = new();
        FailingCreateConnector destinationConnector = new(throwForSourceId: "a1");
        sourceConnector.Seed(CreateContactItem("a1", "v1", "Alice", email: "alice@example.com"));
        sourceConnector.Seed(CreateContactItem("a2", "v1", "Bob", email: "bob@example.com"));

        InMemoryLogger<JobExecutor> logger = new();
        var executor = CreateExecutor(logger);

        var result = await executor.ExecuteAsync("job-1", CreateJob(), sourceConnector, destinationConnector);

        Assert.True(result.Succeeded);
        Assert.Contains(destinationConnector.Items, i => ((CanonicalContact)i.Payload!).DisplayName == "Bob" && !i.IsDeleted);
        Assert.Contains(logger.Entries, e => e.Contains("a1", StringComparison.Ordinal) && e.Contains("failed", StringComparison.Ordinal));
    }

    private JobExecutor CreateExecutor(ILogger<JobExecutor>? logger = null) =>
        new(
            new Planner(NullLogger<Planner>.Instance),
            linkStateRepository,
            endpointCursorRepository,
            operationLogRepository,
            leaseRepository,
            logger ?? NullLogger<JobExecutor>.Instance);

    private static JobOptions CreateJob(ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins) =>
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

    private static CanonicalItem CreateContactItem(string id, string version, string displayName, string? organization = null, string? email = null) =>
        new()
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Version = version,
            Payload = new CanonicalContact
            {
                DisplayName = displayName,
                Organization = organization,
                Emails = [new ContactEmail { Address = email ?? GenerateTestEmail(displayName) }],
            },
        };

    private static string GenerateTestEmail(string displayName) =>
        $"{displayName.Replace(" ", ".", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}@example.com";

    private sealed class PagedConnector(params IncrementalPage[] pages) : IConnector
    {
        private readonly IncrementalPage[] pages = pages;
        private int index;

        public ConnectorCapabilities Capabilities { get; } = new()
        {
            ConnectorType = "paged-test",
            SupportsIncrementalSync = true,
            SupportsDeletes = true,
        };

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(GetPage(reset: true));

        public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(GetPage());

        public Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<CanonicalItem?>(null);

        public Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private IncrementalPage GetPage(bool reset = false)
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
