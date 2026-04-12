namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
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
    public async Task ExportAsyncUsesLastnameFirstnameNaming()
    {
        connectorA.Seed(MakeContact("a1", "Alice", lastName: "Smith"));
        string dir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        try
        {
            await service.ExportAsync("Microsoft", dir);
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Single(files);
            Assert.Equal("smith_alice.json", Path.GetFileName(files[0]));
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
            Assert.Contains("smith_alice.json", files);
            Assert.Contains("smith_alice_2.json", files);
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

    // ── Helpers ───────────────────────────────────────────────────────────

    private static CanonicalItem MakeContact(string id, string firstName, string lastName = "")
    {
        var contact = new CanonicalContact
        {
            GivenName = firstName,
            FamilyName = string.IsNullOrEmpty(lastName) ? null : lastName,
            DisplayName = string.IsNullOrEmpty(lastName) ? firstName : $"{firstName} {lastName}",
        };
        return new CanonicalItem
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Version = "v1",
            Payload = contact,
        };
    }
}
