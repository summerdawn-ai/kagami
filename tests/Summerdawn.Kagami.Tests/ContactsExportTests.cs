using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

/// <summary>
/// Integration tests for the export-via-job-pipeline path in <see cref="CommandHandler{TItem}"/>.
/// </summary>
public sealed class ContactsExportTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly LinkStateRepository linkStateRepo;
    private readonly FakeConnector sourceConnector = new();
    private readonly CommandHandler<CanonicalContact> service;
    private readonly string exportDir;

    public ContactsExportTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        linkStateRepo = new LinkStateRepository(db);

        var connectors = new Dictionary<string, IConnector<CanonicalContact>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Source"] = sourceConnector,
        };

        var executor = new JobExecutor(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            linkStateRepo,
            new EndpointCursorRepository(db),
            new LeaseRepository(db),
            new SyncActionExecutor(linkStateRepo, new OperationLogRepository(db), NullLogger<SyncActionExecutor>.Instance),
            NullLogger<JobExecutor>.Instance);

        service = new CommandHandler<CanonicalContact>(
            name => connectors[name],
            executor,
            db,
            NullLogger<CommandHandler<CanonicalContact>>.Instance);

        exportDir = Path.Combine(Path.GetTempPath(), $"kagami-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportDir);
    }

    public void Dispose()
    {
        databasePath.Dispose();
        if (Directory.Exists(exportDir))
        {
            Directory.Delete(exportDir, true);
        }
    }

    // ── Export: basic pipeline behavior ──────────────────────────────────

    [Fact]
    public async Task ExportAsync_WritesOneJsonFilePerContact()
    {
        sourceConnector.Seed(MakeContact("a1", "Alice", "Smith"));
        sourceConnector.Seed(MakeContact("a2", "Bob", "Jones"));

        var result = await service.ExportAsync("Source", exportDir);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ActionsPlanned);
        var files = Directory.GetFiles(exportDir, "*.json");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public async Task ExportAsync_WritesPhotoFileAlongsideJson()
    {
        var item = MakeContact("a1", "Alice", "Smith");
        ContactPhotoMetadataHelper.SetPhoto(item,
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            "image/png");
        sourceConnector.Seed(item);

        await service.ExportAsync("Source", exportDir);

        Assert.True(File.Exists(Path.Combine(exportDir, "alice_smith.json")));
        Assert.True(File.Exists(Path.Combine(exportDir, "alice_smith.png")));
    }

    // ── Export: do-not-clobber regression tests ───────────────────────────

    /// <summary>
    /// Regression test: existing files in the export directory must be preserved unless
    /// <c>--prune</c> is explicitly set.
    /// </summary>
    [Fact]
    public async Task ExportAsync_WithoutPrune_PreservesExistingFiles()
    {
        string existingFile = Path.Combine(exportDir, "existing.json");
        await File.WriteAllTextAsync(existingFile, "{}");

        sourceConnector.Seed(MakeContact("a1", "Alice", "Smith"));

        var result = await service.ExportAsync("Source", exportDir, prune: false);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(existingFile), "Existing file must be preserved when --prune is not set");
        Assert.True(File.Exists(Path.Combine(exportDir, "alice_smith.json")));
    }

    [Fact]
    public async Task ExportAsync_WithPrune_DeletesStaleFiles()
    {
        string staleFile = Path.Combine(exportDir, "stale.json");
        await File.WriteAllTextAsync(staleFile, "{}");

        sourceConnector.Seed(MakeContact("a1", "Alice", "Smith"));

        var result = await service.ExportAsync("Source", exportDir, prune: true);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(staleFile), "Stale file must be removed when --prune is set");
        Assert.True(File.Exists(Path.Combine(exportDir, "alice_smith.json")));
    }

    [Fact]
    public async Task ExportAsync_WithPrune_DeletesStalePhotoAndJsonTogether()
    {
        string staleJson = Path.Combine(exportDir, "stale.json");
        string stalePhoto = Path.Combine(exportDir, "stale.png");
        await File.WriteAllTextAsync(staleJson, "{}");
        await File.WriteAllBytesAsync(stalePhoto, [0x01]);

        sourceConnector.Seed(MakeContact("a1", "Alice", "Smith"));

        var result = await service.ExportAsync("Source", exportDir, prune: true);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(staleJson), "Stale JSON should be removed");
        Assert.False(File.Exists(stalePhoto), "Stale photo should be removed alongside its JSON");
    }

    // ── Export: NoPersistence — sync DB must not be written ───────────────

    [Fact]
    public async Task ExportAsync_DoesNotWriteSyncDatabase()
    {
        sourceConnector.Seed(MakeContact("a1", "Alice", "Smith"));

        await service.ExportAsync("Source", exportDir);

        // After export, no link-state rows should exist in the DB.
        var partitionKey = $"contact:Source:{exportDir}";
        var links = await linkStateRepo.GetByPartitionAsync(partitionKey);
        Assert.Empty(links);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
