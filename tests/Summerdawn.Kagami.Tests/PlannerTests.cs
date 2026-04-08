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
        var actions = planner.PlanFromSideA(job, [CreateItem("a1")], []);
        Assert.Empty(actions);
    }

    [Fact]
    public void PlanFromSideACreatesWhenNoLinkExists()
    {
        var actions = planner.PlanFromSideA(CreateJob(), [CreateItem("a1")], []);
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

        Assert.Single(planner.PlanFromSideA(CreateJob(SyncMode.AToB), [changedItem], [link]));
        Assert.Empty(planner.PlanFromSideA(CreateJob(SyncMode.BToA), [changedItem], [link]));
    }

    private static JobOptions CreateJob(SyncMode mode = SyncMode.Bidirectional, DeletePolicy deletePolicy = DeletePolicy.Mirror) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.CalendarEvent,
            EndpointA = "endpointA",
            EndpointB = "endpointB",
            SyncMode = mode,
            DeletePolicy = deletePolicy,
        };

    private static CanonicalItem CreateItem(string id, string version = "v1") =>
        new()
        {
            EntityType = EntityType.CalendarEvent,
            SourceId = id,
            Version = version,
        };
}
