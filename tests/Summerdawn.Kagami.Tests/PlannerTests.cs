namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

public sealed class PlannerTests
{
    private readonly Planner planner = new(NullLogger<Planner>.Instance);

    [Fact]
    public void PlanFromSideARespectsDirectionPolicy()
    {
        var job = CreateJob(SyncMode.BToA);
        var actions = planner.PlanFromSideA(job, [CreateItem("a1")], [], []);
        Assert.Empty(actions);
    }

    [Fact]
    public void PlanFromSideACreatesWhenNoLinkExists()
    {
        var actions = planner.PlanFromSideA(CreateJob(), [CreateItem("a1")], [], []);
        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Create, actions[0].Kind);
        Assert.Equal(SyncSide.B, actions[0].TargetSide);
    }

    [Fact]
    public void PlanFromSideADeletesWhenConfiguredToMirrorDeletes()
    {
        var actions = planner.PlanFromSideA(
            CreateJob(deletePolicy: DeletePolicy.Mirror),
            [new CanonicalItem { EntityType = EntityType.CalendarEvent, SourceId = "a1", IsDeleted = true }],
            [],
            [new LinkStateRow { JobKey = "job-1", EntityType = EntityType.CalendarEvent, SideAId = "a1", SideBId = "b1" }]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Delete, actions[0].Kind);
        Assert.Equal("b1", actions[0].DeleteId);
    }

    [Fact]
    public void PlannerDerivesBehaviorFromCurrentJobConfiguration()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
        };

        var changedItem = CreateItem("a1", "v2");

        Assert.Single(planner.PlanFromSideA(CreateJob(SyncMode.AToB), [changedItem], [], [link]));
        Assert.Empty(planner.PlanFromSideA(CreateJob(SyncMode.BToA), [changedItem], [], [link]));
    }

    [Fact]
    public void PlanFromSideAResolvesConflictWhenTargetAlsoChanged()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
        };

        var sourceItem = CreateItem("a1", "v2", lastModified: DateTimeOffset.UtcNow.AddMinutes(-5));
        var targetItem = CreateItem("b1", "v2b", lastModified: DateTimeOffset.UtcNow);

        var actions = planner.PlanFromSideA(
            CreateJob(conflictPolicy: ConflictPolicy.Skip),
            [sourceItem],
            [targetItem],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.NoOp, actions[0].Kind);
    }

    [Fact]
    public void PlanFromSideAResolvesConflictWithSideAWinsPolicy()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
        };

        var actions = planner.PlanFromSideA(
            CreateJob(conflictPolicy: ConflictPolicy.SideAWins),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v2b")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
    }

    [Fact]
    public void PlanFromSideBResolvesConflictWithSideAWinsPolicy()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
        };

        var actions = planner.PlanFromSideB(
            CreateJob(conflictPolicy: ConflictPolicy.SideAWins),
            [CreateItem("b1", "v2")],
            [CreateItem("a1", "v2a")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.NoOp, actions[0].Kind);
    }

    [Fact]
    public void PlanFromSideAResolvesConflictWithSideBWinsPolicy()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
        };

        var actions = planner.PlanFromSideA(
            CreateJob(conflictPolicy: ConflictPolicy.SideBWins),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v2b")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.NoOp, actions[0].Kind);
    }

    [Fact]
    public void PlanFromSideBResolvesConflictWithSideBWinsPolicy()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
        };

        var actions = planner.PlanFromSideB(
            CreateJob(conflictPolicy: ConflictPolicy.SideBWins),
            [CreateItem("b1", "v2")],
            [CreateItem("a1", "v2a")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
    }

    [Fact]
    public void PlanFromSideAUpdatesWhenOnlySourceChanged()
    {
        var link = new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
        };

        var actions = planner.PlanFromSideA(
            CreateJob(),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v1")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
    }

    private static JobOptions CreateJob(
        SyncMode mode = SyncMode.Bidirectional,
        DeletePolicy deletePolicy = DeletePolicy.Mirror,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.CalendarEvent,
            EndpointA = "endpointA",
            EndpointB = "endpointB",
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
        };

    private static CanonicalItem CreateItem(string id, string version = "v1", DateTimeOffset? lastModified = null) =>
        new()
        {
            EntityType = EntityType.CalendarEvent,
            SourceId = id,
            Version = version,
            Payload = new CanonicalCalendarEvent
            {
                Subject = id,
                Start = DateTimeOffset.UtcNow,
                End = DateTimeOffset.UtcNow.AddHours(1),
                LastModified = lastModified,
            },
        };
}
