namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

public sealed class ContactsImportTests : IDisposable
{
    private static readonly byte[] OnePxPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,  // PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,  // IHDR chunk
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,  // IDAT chunk
        0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC,
        0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,  // IEND chunk
        0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly FakeConnector connectorDest = new();
    private readonly FakeConnectorFactory factory;
    private readonly ContactsService service;
    private readonly string importDir;

    public ContactsImportTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();

        factory = new FakeConnectorFactory();
        factory.Register("Destination", connectorDest);

        var options = new KagamiOptions
        {
            Endpoints =
            {
                ["Destination"] = new EndpointOptions { Type = "fake", Credential = string.Empty },
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

        importDir = Path.Combine(Path.GetTempPath(), $"kagami-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(importDir);
    }

    public void Dispose()
    {
        databasePath.Dispose();
        if (Directory.Exists(importDir))
        {
            Directory.Delete(importDir, true);
        }
    }

    // ── ImportAsync: photo handling ────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_ClearsPhoto_WhenNoPhotoFileAlongsideJson()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result = await service.ImportAsync(importDir, "Destination");

        Assert.Equal(1, result.Created);
        CanonicalItem created = Assert.Single(connectorDest.Items);
        Assert.False(ContactPhotoMetadata.TryGetPhoto(created, out _, out _),
            "Photo should be explicitly cleared when no image file is present");
    }

    [Fact]
    public async Task ImportAsync_SetsPhoto_WhenPhotoFileAlongsideJson()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });
        await File.WriteAllBytesAsync(Path.Combine(importDir, "alice_smith.png"), OnePxPng);

        var result = await service.ImportAsync(importDir, "Destination");

        Assert.Equal(1, result.Created);
        CanonicalItem created = Assert.Single(connectorDest.Items);
        Assert.True(ContactPhotoMetadata.TryGetPhoto(created, out byte[] photoBytes, out string contentType));
        Assert.Equal("image/png", contentType);
        Assert.Equal(OnePxPng.Length, photoBytes.Length);
    }

    // ── ImportAsync: prune behavior ───────────────────────────────────────

    [Fact]
    public async Task ImportAsync_WithPrune_DeletesContactsNotInImportSet()
    {
        // Pre-populate destination with a contact that is NOT in the import folder
        connectorDest.Seed(MakeContact("dest1", "Bob", "Jones"));

        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result = await service.ImportAsync(importDir, "Destination", prune: true);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Deleted);
        // Bob Jones should be deleted, Alice Smith should exist
        Assert.Single(connectorDest.Items, i => !i.IsDeleted);
    }

    [Fact]
    public async Task ImportAsync_WithoutPrune_LeavesContactsNotInImportSetAlone()
    {
        // Pre-populate destination with a contact that is NOT in the import folder
        connectorDest.Seed(MakeContact("dest1", "Bob", "Jones"));

        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result = await service.ImportAsync(importDir, "Destination", prune: false);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Deleted);
        // Both Bob Jones and Alice Smith should exist (Bob was not pruned)
        Assert.Equal(2, connectorDest.Items.Count(i => !i.IsDeleted));
    }

    // ── ImportAsync: idempotency ───────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_IsIdempotent()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result1 = await service.ImportAsync(importDir, "Destination");
        Assert.Equal(1, result1.Created);
        Assert.Equal(1, connectorDest.Items.Count(i => !i.IsDeleted));

        // Second import: should update (match) not create a second copy
        var result2 = await service.ImportAsync(importDir, "Destination");
        Assert.Equal(0, result2.Created);
        Assert.Equal(1, result2.Updated);
        Assert.Equal(1, connectorDest.Items.Count(i => !i.IsDeleted));
    }

    // ── ImportAsync: whatIf ───────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_WhatIf_DoesNotWriteToDestination()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result = await service.ImportAsync(importDir, "Destination", whatIf: true);

        Assert.Equal(1, result.Created);
        Assert.Empty(connectorDest.Items);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task WriteContactJsonAsync(string baseName, object contact)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(contact, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
        await File.WriteAllTextAsync(Path.Combine(importDir, $"{baseName}.json"), json);
    }

    private static CanonicalItem MakeContact(string id, string firstName, string lastName)
    {
        var contact = new CanonicalContact
        {
            GivenName = firstName,
            FamilyName = lastName,
            DisplayName = $"{firstName} {lastName}",
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
