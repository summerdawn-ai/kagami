using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class EventsExportTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly FakeEventConnector sourceConnector = new();
    private readonly CommandHandler<CanonicalEvent> service;
    private readonly string exportDir;

    public EventsExportTests()
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
            _ => sourceConnector,
            executor,
            db,
            NullLogger<CommandHandler<CanonicalEvent>>.Instance);

        exportDir = Path.Combine(Path.GetTempPath(), $"kagami-event-export-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task ExportAsync_WritesOneJsonFilePerEvent()
    {
        sourceConnector.Seed(MakeEvent("event-1", "Design Review"));

        var result = await service.ExportAsync("Source", exportDir);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ActionsPlanned);
        Assert.Single(Directory.GetFiles(exportDir, "*.json"));
    }

    [Fact]
    public async Task ExportAsync_UsesTruncatedTitleAndStartTimeForFileName()
    {
        sourceConnector.Seed(MakeEvent("event-1", "Quarterly Planning Session With Stakeholders"));

        await service.ExportAsync("Source", exportDir);

        string[] files = Directory.GetFiles(exportDir, "*.json");
        Assert.Single(files);
        Assert.Equal("quarterly_planning_s_20260514_1530.json", Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task ExportAsync_PreservesSourceEndpointNameInJsonProvenance()
    {
        sourceConnector.Seed(MakeEvent("event-1", "Design Review", endpointName: "SourceCalendar"));

        await service.ExportAsync("Source", exportDir);

        string exportFile = Assert.Single(Directory.GetFiles(exportDir, "*.json"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exportFile));
        Assert.Equal("SourceCalendar", document.RootElement.GetProperty("provenance").GetProperty("endpointName").GetString());
    }

    [Fact]
    public async Task ExportAsync_WithPrune_DeletesStaleFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(exportDir, "stale.json"), "{}");
        sourceConnector.Seed(MakeEvent("event-1", "Design Review"));

        var result = await service.ExportAsync("Source", exportDir, prune: true);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(exportDir, "stale.json")));
        Assert.Single(Directory.GetFiles(exportDir, "*.json"));
    }

    private static CanonicalEvent MakeEvent(string id, string title, string? endpointName = "fake") => new()
    {
        Title = title,
        Description = "Agenda",
        From = new DateTimeOffset(2026, 05, 14, 15, 30, 00, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 05, 14, 16, 00, 00, TimeSpan.Zero),
        Location = "Zoom",
        ICalUid = $"{id}@example.test",
        Provenance =
        {
            ProviderId = id,
            EndpointName = endpointName,
            Version = "v1",
        }
    };
}
