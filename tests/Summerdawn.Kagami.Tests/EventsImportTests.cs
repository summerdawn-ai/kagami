using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Serialization;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class EventsImportTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly FakeEventConnector destinationConnector = new();
    private readonly CommandHandler<CanonicalEvent> service;
    private readonly string importDir;

    public EventsImportTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();

        var executor = new JobExecutor(
            new SyncActionPlanner(new LinkCreator(NullLogger<LinkCreator>.Instance)),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new LeaseRepository(db),
            new SyncActionExecutor(new LinkStateRepository(db), new OperationLogRepository(db), NullLogger<SyncActionExecutor>.Instance),
            db,
            NullLogger<JobExecutor>.Instance);

        service = new CommandHandler<CanonicalEvent>(
            _ => destinationConnector,
            executor,
            db,
            NullLogger<CommandHandler<CanonicalEvent>>.Instance);

        importDir = Path.Combine(Path.GetTempPath(), $"kagami-event-import-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task ImportAsync_CreatesEventsFromJsonFiles()
    {
        await WriteEventJsonAsync("design_review_20260514_1530", MakeEvent("export-1", "Design Review"));

        var result = await service.ImportAsync(importDir, "Destination");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        var created = Assert.Single(destinationConnector.Items, item => !item.IsDeleted);
        Assert.Equal("Design Review", created.Title);
        Assert.Equal("design-review@example.test", created.ICalUid);
    }

    [Fact]
    public async Task ImportAsync_WithPrune_DeletesEventsNotInImportSet()
    {
        destinationConnector.Seed(MakeEvent("dest-1", "Old Event"));
        await WriteEventJsonAsync("design_review_20260514_1530", MakeEvent("export-1", "Design Review"));

        var result = await service.ImportAsync(importDir, "Destination", prune: true);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ActionsPlanned);
        Assert.Single(destinationConnector.Items, item => !item.IsDeleted);
    }

    private async Task WriteEventJsonAsync(string baseName, CanonicalEvent calendarEvent)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(calendarEvent, KagamiJsonContext.ImportExportJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(importDir, $"{baseName}.json"), json);
    }

    private static CanonicalEvent MakeEvent(string id, string title) => new()
    {
        Title = title,
        Description = "Agenda",
        From = new DateTimeOffset(2026, 05, 14, 15, 30, 00, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 05, 14, 16, 00, 00, TimeSpan.Zero),
        Location = "Zoom",
        ICalUid = $"{title.ToLowerInvariant().Replace(' ', '-')}@example.test",
        Provenance =
        {
            ProviderId = id,
            Version = "v1",
        }
    };
}
