using System.Text.Json;
using System.Text.Encodings.Web;

using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly FakeConnector connectorDest = new();
    private readonly CommandHandler<CanonicalContact> service;
    private readonly string importDir;

    public ContactsImportTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();

        var connectors = new Dictionary<string, IConnector<CanonicalContact>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Destination"] = connectorDest,
        };

        var executor = new JobExecutor(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new LeaseRepository(db),
            new SyncActionExecutor(new LinkStateRepository(db), new OperationLogRepository(db), NullLogger<SyncActionExecutor>.Instance),
            NullLogger<JobExecutor>.Instance);

        service = new CommandHandler<CanonicalContact>(
            name => connectors[name],
            executor,
            db,
            NullLogger<CommandHandler<CanonicalContact>>.Instance);

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

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        var created = Assert.Single(connectorDest.Items);
        Assert.False(ContactPhotoMetadataHelper.TryGetPhoto(created, out _, out _),
            "Imported contact should have no photo when no image file is present");
    }

    [Fact]
    public async Task ImportAsync_SetsPhoto_WhenPhotoFileAlongsideJson()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });
        await File.WriteAllBytesAsync(Path.Combine(importDir, "alice_smith.png"), OnePxPng);

        var result = await service.ImportAsync(importDir, "Destination");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        var created = Assert.Single(connectorDest.Items);
        Assert.True(ContactPhotoMetadataHelper.TryGetPhoto(created, out byte[] photoBytes, out string contentType));
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

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ActionsPlanned); // 1 create + 1 delete
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

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned); // only 1 create (Bob not pruned)
        // Both Bob Jones and Alice Smith should exist (Bob was not pruned)
        Assert.Equal(2, connectorDest.Items.Count(i => !i.IsDeleted));
    }

    // ── ImportAsync: idempotency ───────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_IsIdempotent()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result1 = await service.ImportAsync(importDir, "Destination");
        Assert.True(result1.Succeeded);
        Assert.Equal(1, result1.ActionsPlanned);
        Assert.Equal(1, connectorDest.Items.Count(i => !i.IsDeleted));

        // Second import: content is unchanged so no actions are planned
        var result2 = await service.ImportAsync(importDir, "Destination");
        Assert.True(result2.Succeeded);
        Assert.Equal(0, result2.ActionsPlanned); // skip, content unchanged
        Assert.Equal(1, connectorDest.Items.Count(i => !i.IsDeleted));
    }

    // ── ImportAsync: whatIf ───────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_WhatIf_DoesNotWriteToDestination()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });

        var result = await service.ImportAsync(importDir, "Destination", whatIf: true);

        Assert.Equal(1, result.ActionsPlanned);
        Assert.Empty(connectorDest.Items);
    }

    // ── ImportAsync: error handling ───────────────────────────────────────

    [Fact]
    public async Task ImportAsync_SingleContactCreateFailure_ContinuesToNextContact()
    {
        await WriteContactJsonAsync("alice_smith", new { displayName = "Alice Smith", givenName = "Alice", familyName = "Smith" });
        await WriteContactJsonAsync("bob_jones", new { displayName = "Bob Jones", givenName = "Bob", familyName = "Jones" });

        FailingCreateConnector failingConnector = new(throwForSourceId: "alice_smith");
        var failingService = CreateServiceWith(failingConnector);

        var result = await failingService.ImportAsync(importDir, "Destination");

        // Job faults because alice_smith's create threw, but processing continued for bob_jones
        Assert.False(result.Succeeded);
        Assert.Contains(failingConnector.Items, i => i is { DisplayName: "Bob Jones", IsDeleted: false });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private CommandHandler<CanonicalContact> CreateServiceWith(IConnector<CanonicalContact> destinationConnector)
    {
        var executor = new JobExecutor(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new LeaseRepository(db),
            new SyncActionExecutor(new LinkStateRepository(db), new OperationLogRepository(db), NullLogger<SyncActionExecutor>.Instance),
            NullLogger<JobExecutor>.Instance);

        return new CommandHandler<CanonicalContact>(
            _ => destinationConnector,
            executor,
            db,
            NullLogger<CommandHandler<CanonicalContact>>.Instance);
    }

    private async Task WriteContactJsonAsync(string baseName, object contact)
    {
        string json = JsonSerializer.Serialize(contact, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(importDir, $"{baseName}.json"), json);
    }

    [Fact]
    public async Task ImportAsync_WritesReadablePlusSignsInExportedJson()
    {
        var contact = MakeContact("plus1", "C++", "Team");

        var connector = new ImportExportContactsConnector(importDir);

        var exported = await connector.CreateItemAsync(contact);

        string json = await File.ReadAllTextAsync(Path.Combine(importDir, $"{exported.Provenance.ProviderId}.json"));
        Assert.Contains("C++", json);
        Assert.DoesNotContain("\\u002B", json);
    }

    private static CanonicalContact MakeContact(string id, string firstName, string lastName) => new()
    {
        GivenName = firstName,
        FamilyName = lastName,
        DisplayName = $"{firstName} {lastName}",

        Provenance =
        {
            ProviderId = id,
            Version = "v1",
        }
    };
}
