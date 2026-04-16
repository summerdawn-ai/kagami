namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

public sealed class ContactsServiceTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly FakeConnector connectorA = new();
    private readonly FakeConnector connectorB = new();
    private readonly FakeConnectorFactory factory;
    private readonly ContactsService service;

    public ContactsServiceTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();

        factory = new FakeConnectorFactory();
        factory.Register("Microsoft", connectorA);
        factory.Register("Google", connectorB);

        var options = new KagamiOptions
        {
            Endpoints =
            {
                ["Microsoft"] = new EndpointOptions { Type = "fake", Credential = string.Empty },
                ["Google"] = new EndpointOptions { Type = "fake", Credential = string.Empty },
            },
        };

        var executor = new JobExecutor(
            new Planner(NullLogger<Planner>.Instance),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new OperationLogRepository(db),
            new LeaseRepository(db),
            NullLogger<JobExecutor>.Instance);

        service = new ContactsService(
            options,
            factory,
            executor,
            db,
            NullLogger<ContactsService>.Instance);
    }

    public void Dispose() => databasePath.Dispose();

    // ── ListAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsyncReturnsAllContacts()
    {
        connectorA.Seed(MakeContact("a1", "Alice"));
        connectorA.Seed(MakeContact("a2", "Bob"));

        var items = await service.ListAsync("Microsoft");

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task ListAsyncReturnsAllContactsAcrossPages()
    {
        ContactsService pagedService = CreateService(
            new PagedConnector(
                new IncrementalPage
                {
                    Items = [MakeContact("a1", "Alice")],
                    HasMore = true,
                    NextCursor = "page-2",
                },
                new IncrementalPage
                {
                    Items = [MakeContact("a2", "Bob")],
                    HasMore = false,
                    NextCursor = "delta-token",
                }));

        var items = await pagedService.ListAsync("Microsoft");

        Assert.Equal(2, items.Count);
        Assert.Equal(["a1", "a2"], items.Select(item => item.SourceId));
    }

    [Fact]
    public async Task ListAsyncHonorsMaxItemsAcrossPages()
    {
        ContactsService pagedService = CreateService(
            new PagedConnector(
                new IncrementalPage
                {
                    Items = Enumerable.Range(1, 60).Select(index => MakeContact($"a{index}", $"Contact {index}")).ToArray(),
                    HasMore = true,
                    NextCursor = "page-2",
                },
                new IncrementalPage
                {
                    Items = Enumerable.Range(61, 60).Select(index => MakeContact($"a{index}", $"Contact {index}")).ToArray(),
                    HasMore = false,
                    NextCursor = "delta-token",
                }));

        var items = await pagedService.ListAsync("Microsoft", filter: null, maxItems: 100);

        Assert.Equal(100, items.Count);
        Assert.Equal("a100", items[^1].SourceId);
    }

    [Fact]
    public async Task ListAsyncAppliesFilter()
    {
        connectorA.Seed(MakeContact("a1", "Alice"));
        connectorA.Seed(MakeContact("a2", "Bob"));

        var filter = ContactFilter.Parse("startswith(name,'A')")!;
        var items = await service.ListAsync("Microsoft", filter);

        Assert.Single(items);
        Assert.Equal("Alice", ((CanonicalContact)items[0].Payload!).DisplayName);
    }

    [Fact]
    public async Task ListAsyncAppliesFilterUsingOrganizationWhenDisplayNameIsEmpty()
    {
        connectorA.Seed(MakeContact("a1", displayName: string.Empty, organization: "Contoso Ltd"));
        connectorA.Seed(MakeContact("a2", "Bob"));

        var filter = ContactFilter.Parse("contains(name,'Contoso')")!;
        var items = await service.ListAsync("Microsoft", filter);

        Assert.Single(items);
        Assert.Equal("Contoso Ltd", ((CanonicalContact)items[0].Payload!).Organization);
    }

    [Fact]
    public async Task ListAsyncWithFilterLoadsPhotosOnlyForMatchedContacts()
    {
        LazyPhotoConnector lazyConnector = new(
            MakeContact("a1", "Alice"),
            MakeContact("a2", "Bob"));
        ContactsService lazyService = CreateService(lazyConnector);

        var filter = ContactFilter.Parse("startswith(name,'A')")!;
        IReadOnlyList<CanonicalItem> items = await lazyService.ListAsync("Microsoft", filter);

        CanonicalItem item = Assert.Single(items);
        Assert.Equal("Alice", ((CanonicalContact)item.Payload!).DisplayName);
        Assert.Equal(["a1"], lazyConnector.LoadedPhotoIds);
    }

    [Fact]
    public async Task ListAsyncThrowsForUnknownEndpoint()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListAsync("Unknown"));
    }

    // ── ExportAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExportAsyncWritesOneFilePerContact()
    {
        connectorA.Seed(MakeContact("a1", "Alice", lastName: "Smith"));
        connectorA.Seed(MakeContact("a2", "Bob", lastName: "Jones"));
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Equal(2, files.Length);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncUsesDisplayNameForFileName()
    {
        connectorA.Seed(MakeContact("a1", "Alice", lastName: "Smith"));
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Single(files);
            Assert.Equal("alice_smith.json", Path.GetFileName(files[0]));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncAddsSuffixForDuplicateNames()
    {
        connectorA.Seed(MakeContact("a1", "Alice", lastName: "Smith"));
        connectorA.Seed(MakeContact("a2", "Alice", lastName: "Smith"));
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            var files = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).Order().ToList();
            Assert.Contains("alice_smith.json", files);
            Assert.Contains("alice_smith_2.json", files);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncDeletesExistingFilesBeforeWriting()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string stale = Path.Combine(dir, "stale.json");
        await File.WriteAllTextAsync(stale, "{}");

        connectorA.Seed(MakeContact("a1", "Alice", lastName: "Smith"));
        try
        {
            await service.ExportAsync("Microsoft", dir);
            Assert.False(File.Exists(stale), "Stale export file should have been deleted");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncUsesOrganizationWhenDisplayNameIsEmpty()
    {
        connectorA.Seed(MakeContact("a1", displayName: string.Empty, organization: "Contoso Ltd"));
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Single(files);
            Assert.Equal("contoso_ltd.json", Path.GetFileName(files[0]));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncFallsBackToIdWhenNameIsMissing()
    {
        connectorA.Seed(MakeContact("contact-42", displayName: string.Empty));
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Single(files);
            Assert.Equal("contact_42.json", Path.GetFileName(files[0]));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncWritesPhotoNextToJson()
    {
        CanonicalItem item = MakeContact("a1", "Alice", lastName: "Smith");
        ContactPhotoMetadata.SetPhoto(item, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png");
        connectorA.Seed(item);

        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            Assert.True(File.Exists(Path.Combine(dir, "alice_smith.json")));
            Assert.True(File.Exists(Path.Combine(dir, "alice_smith.png")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExportAsyncDeletesExistingPhotoFilesBeforeWriting()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string stalePhoto = Path.Combine(dir, "stale.png");
        await File.WriteAllBytesAsync(stalePhoto, [0x01]);

        CanonicalItem item = MakeContact("a1", "Alice", lastName: "Smith");
        ContactPhotoMetadata.SetPhoto(item, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png");
        connectorA.Seed(item);
        try
        {
            await service.ExportAsync("Microsoft", dir);
            Assert.False(File.Exists(stalePhoto), "Stale export photo should have been deleted");
            Assert.True(File.Exists(Path.Combine(dir, "alice_smith.png")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    // ── SyncAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsyncCreatesContactsOnDestination()
    {
        connectorA.Seed(MakeContact("a1", "Alice"));

        var result = await service.SyncAsync("Microsoft", "Google");

        Assert.True(result.Succeeded);
        Assert.Contains(connectorB.Items, i => !i.IsDeleted);
    }

    [Fact]
    public async Task SyncAsyncWhatIfDoesNotWrite()
    {
        connectorA.Seed(MakeContact("a1", "Alice"));

        var result = await service.SyncAsync("Microsoft", "Google", whatIf: true);

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Empty(connectorB.Items);
    }

    [Fact]
    public async Task SyncAsyncForceResyncAfterInitialSync()
    {
        connectorA.Seed(MakeContact("a1", "Alice"));
        await service.SyncAsync("Microsoft", "Google");

        // Normal second run: nothing to do
        var normal = await service.SyncAsync("Microsoft", "Google");
        Assert.Equal(0, normal.ActionsPlanned);

        // Force run: re-evaluate all contacts
        var forced = await service.SyncAsync("Microsoft", "Google", force: true);
        Assert.True(forced.ActionsPlanned > 0);
    }

    [Fact]
    public async Task SyncAsyncLinksSingleDuplicateMatchInsteadOfCreating()
    {
        connectorA.Seed(MakeContact("a1", "Alice", email: "alice@example.com", phone: "+1 (555) 123-4567"));
        connectorB.Seed(MakeContact("b1", "Alice", email: " Alice@example.com ", phone: "15551234567"));

        var result = await service.SyncAsync("Microsoft", "Google");
        var links = await new LinkStateRepository(db).GetByJobAsync("contacts:Microsoft:Google");

        Assert.True(result.Succeeded);
        Assert.Single(connectorB.Items, item => !item.IsDeleted);
        Assert.Single(links);
        Assert.Equal("a1", links[0].SourceId);
        Assert.Equal("b1", links[0].DestinationId);
    }

    [Fact]
    public async Task SyncAsyncSkipsAutoLinkingWhenMultipleDuplicateMatchesExist()
    {
        connectorA.Seed(MakeContact("a1", "Alice", email: "alice@example.com"));
        connectorB.Seed(MakeContact("b1", "Alice", email: "alice@example.com"));
        connectorB.Seed(MakeContact("b2", "Alice", email: "alice@example.com"));

        var result = await service.SyncAsync("Microsoft", "Google");
        var links = await new LinkStateRepository(db).GetByJobAsync("contacts:Microsoft:Google");

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Equal(2, connectorB.Items.Count(item => !item.IsDeleted));
        Assert.Empty(links);
    }

    [Fact]
    public async Task SyncAsyncCopiesPhotoMetadataToDestination()
    {
        CanonicalItem source = MakeContact("a1", "Alice", lastName: "Smith");
        ContactPhotoMetadata.SetPhoto(source, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png");
        connectorA.Seed(source);

        JobExecutionResult result = await service.SyncAsync("Microsoft", "Google");

        Assert.True(result.Succeeded);
        CanonicalItem created = Assert.Single(connectorB.Items);
        Assert.True(ContactPhotoMetadata.TryGetPhoto(created, out byte[] photoBytes, out string contentType));
        Assert.Equal("image/png", contentType);
        Assert.Equal(source.Metadata["contact.photo.bytes"], created.Metadata["contact.photo.bytes"]);
        Assert.Equal(8, photoBytes.Length);
    }

    [Fact]
    public async Task SyncAsyncWithFilterLoadsPhotosOnlyForMatchedContacts()
    {
        LazyPhotoConnector lazyConnector = new(
            MakeContact("a1", "Alice"),
            MakeContact("a2", "Bob"));
        ContactsService lazyService = CreateService(lazyConnector);
        var filter = ContactFilter.Parse("startswith(name,'A')")!;

        JobExecutionResult result = await lazyService.SyncAsync("Microsoft", "Google", filter: filter);

        Assert.True(result.Succeeded);
        Assert.Equal(["a1"], lazyConnector.LoadedPhotoIds);
        CanonicalItem created = Assert.Single(connectorB.Items);
        Assert.Equal("Alice", ((CanonicalContact)created.Payload!).DisplayName);
        Assert.True(ContactPhotoMetadata.TryGetPhoto(created, out _, out _));
    }

    private ContactsService CreateService(IConnector microsoftConnector)
    {
        FakeConnectorFactory pagedFactory = new();
        pagedFactory.Register("Microsoft", microsoftConnector);
        pagedFactory.Register("Google", connectorB);

        var options = new KagamiOptions
        {
            Endpoints =
            {
                ["Microsoft"] = new EndpointOptions { Type = "fake", Credential = string.Empty },
                ["Google"] = new EndpointOptions { Type = "fake", Credential = string.Empty },
            },
        };

        var executor = new JobExecutor(
            new Planner(NullLogger<Planner>.Instance),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new OperationLogRepository(db),
            new LeaseRepository(db),
            NullLogger<JobExecutor>.Instance);

        return new ContactsService(
            options,
            pagedFactory,
            executor,
            db,
            NullLogger<ContactsService>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static CanonicalItem MakeContact(
        string id,
        string displayName,
        string lastName = "",
        string? organization = null,
        string? email = null,
        string? phone = null)
    {
        var contact = new CanonicalContact
        {
            GivenName = displayName,
            FamilyName = string.IsNullOrEmpty(lastName) ? null : lastName,
            DisplayName = string.IsNullOrEmpty(lastName) ? displayName : $"{displayName} {lastName}",
            Organization = organization,
            Emails = email is null ? [] : [new ContactEmail { Address = email }],
            Phones = phone is null ? [] : [new ContactPhone { Number = phone }],
        };
        return new CanonicalItem
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Version = "v1",
            Payload = contact,
        };
    }

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

    private sealed class LazyPhotoConnector(params CanonicalItem[] items) : IConnector
    {
        public ConnectorCapabilities Capabilities { get; } = new()
        {
            ConnectorType = "lazy-photo-test",
            SupportsIncrementalSync = true,
            SupportsDeletes = true,
            SupportsContactPhotos = true,
        };

        public List<string> LoadedPhotoIds { get; } = [];

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default)
        {
            foreach (CanonicalItem item in items)
            {
                ContactPhotoLoader.Attach(item, _ =>
                {
                    LoadedPhotoIds.Add(item.SourceId);
                    ContactPhotoMetadata.SetPhoto(item, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png");
                    return Task.CompletedTask;
                });
            }

            return Task.FromResult(new IncrementalPage
            {
                Items = items,
                NextCursor = "1",
                HasMore = false,
            });
        }

        public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IncrementalPage
            {
                Items = [],
                NextCursor = cursor,
                HasMore = false,
            });

        public Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.FirstOrDefault(item => item.SourceId == id));

        public Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(item);

        public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
