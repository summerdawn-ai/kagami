
using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestDoubles;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class CalendarServiceTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly FakeConnector sourceConnector = new();
    private readonly FakeConnector destinationConnector = new();
    private readonly CalendarService service;

    public CalendarServiceTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();

        var connectors = new Dictionary<string, IConnector>(StringComparer.OrdinalIgnoreCase)
        {
            ["Google"] = sourceConnector,
            ["Microsoft"] = destinationConnector,
        };

        var executor = new JobExecutor(
            new Planner(NullLogger<Planner>.Instance),
            new LinkStateRepository(db),
            new EndpointCursorRepository(db),
            new OperationLogRepository(db),
            new LeaseRepository(db),
            NullLogger<JobExecutor>.Instance);

        service = new CalendarService(
            name => connectors[name],
            executor,
            db,
            NullLogger<CalendarService>.Instance);
    }

    public void Dispose() => databasePath.Dispose();

    // ── ListAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsAllEvents()
    {
        sourceConnector.Seed(MakeEvent("e1", "Team Meeting"));
        sourceConnector.Seed(MakeEvent("e2", "Sprint Planning"));

        var items = await service.ListAsync("Google");

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task ListAsync_ExcludesDeletedEvents()
    {
        sourceConnector.Seed(MakeEvent("e1", "Meeting"));
        sourceConnector.MarkDeleted("e1");

        var items = await service.ListAsync("Google");

        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_HonorsMaxItems()
    {
        for (int i = 0; i < 10; i++)
        {
            sourceConnector.Seed(MakeEvent($"e{i}", $"Event {i}"));
        }

        var items = await service.ListAsync("Google", maxItems: 5);

        Assert.Equal(5, items.Count);
    }

    [Fact]
    public async Task ListAsync_NegativeMaxItems_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ListAsync("Google", maxItems: -1));
    }

    // ── SyncAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_ForwardSync_CopiesEventToDestination()
    {
        sourceConnector.Seed(MakeEvent("e1", "Team Meeting"));

        var result = await service.SyncAsync("Google", "Microsoft");

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Single(destinationConnector.Items);
    }

    [Fact]
    public async Task SyncAsync_WhatIf_DoesNotWriteToDestination()
    {
        sourceConnector.Seed(MakeEvent("e1", "Team Meeting"));

        var result = await service.SyncAsync("Google", "Microsoft", whatIf: true);

        Assert.True(result.Succeeded);
        Assert.True(result.ActionsPlanned > 0);
        Assert.Empty(destinationConnector.Items);
    }

    [Fact]
    public async Task SyncAsync_Bidirectional_SyncsInBothDirections()
    {
        sourceConnector.Seed(MakeEvent("e1", "Meeting A"));
        destinationConnector.Seed(MakeEvent("e2", "Meeting B"));

        var result = await service.SyncAsync("Google", "Microsoft", mode: SyncMode.Bidirectional);

        Assert.True(result.Succeeded);
        Assert.Equal(2, sourceConnector.Items.Count(i => !i.IsDeleted));
        Assert.Equal(2, destinationConnector.Items.Count(i => !i.IsDeleted));
    }

    [Fact]
    public async Task SyncAsync_WithMirrorDelete_PropagatesDeletion()
    {
        sourceConnector.Seed(MakeEvent("e1", "Meeting"));
        await service.SyncAsync("Google", "Microsoft");

        // Now delete from source
        sourceConnector.MarkDeleted("e1");

        var result = await service.SyncAsync(
            "Google",
            "Microsoft",
            deletePolicy: DeletePolicy.Mirror);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(destinationConnector.Items, i => !i.IsDeleted);
    }

    [Fact]
    public async Task SyncAsync_SecondRun_DoesNotDuplicateEvents()
    {
        sourceConnector.Seed(MakeEvent("e1", "Standup"));
        await service.SyncAsync("Google", "Microsoft");

        await service.SyncAsync("Google", "Microsoft");

        Assert.Single(destinationConnector.Items, i => !i.IsDeleted);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static CanonicalItem MakeEvent(string id, string subject) =>
        new()
        {
            EntityType = EntityType.CalendarEvent,
            SourceId = id,
            Version = "v1",
            Payload = new CanonicalCalendarEvent
            {
                Subject = subject,
                Start = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero),
            },
        };
}
