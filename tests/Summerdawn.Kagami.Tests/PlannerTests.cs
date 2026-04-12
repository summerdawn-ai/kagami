namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

public sealed class PlannerTests
{
    private readonly Planner planner = new(NullLogger<Planner>.Instance);

    // -------------------------------------------------------------------------
    // Directional mode / basic create behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_ReturnsEmpty_WhenModeIsBToAAndOnlyASideHasItems()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.BToA), [CreateItem("a1")], [], []);
        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_ReturnsEmpty_WhenModeIsAtoBAndOnlyBSideHasItems()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.AToB), [], [CreateItem("b1")], []);
        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_CreatesOnB_WhenUnlinkedAItemInAtoBMode()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.AToB), [CreateItem("a1")], [], []);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Create, actions[0].Kind);
        Assert.Equal(SyncSide.B, actions[0].TargetSide);
    }

    [Fact]
    public void PlanActions_CreatesOnA_WhenUnlinkedBItemInBToAMode()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.BToA), [], [CreateItem("b1")], []);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Create, actions[0].Kind);
        Assert.Equal(SyncSide.A, actions[0].TargetSide);
    }

    [Fact]
    public void PlanActions_CreatesBothDirections_WhenBidirectionalAndBothUnlinked()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.Bidirectional), [CreateItem("a1")], [CreateItem("b1")], []);

        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, a => a.Kind == SyncActionKind.Create && a.TargetSide == SyncSide.B);
        Assert.Contains(actions, a => a.Kind == SyncActionKind.Create && a.TargetSide == SyncSide.A);
    }

    // -------------------------------------------------------------------------
    // Delete behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_DeletesLinkedTarget_WhenSourceDeletedAndMirrorEnabled()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(deletePolicy: DeletePolicy.Mirror),
            [new CanonicalItem { EntityType = EntityType.CalendarEvent, SourceId = "a1", IsDeleted = true }],
            [],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Delete, actions[0].Kind);
        Assert.Equal("b1", actions[0].DeleteId);
    }

    [Fact]
    public void PlanActions_SkipsDelete_WhenDeletePolicyIgnore()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(deletePolicy: DeletePolicy.Ignore),
            [new CanonicalItem { EntityType = EntityType.CalendarEvent, SourceId = "a1", IsDeleted = true }],
            [],
            [link]);

        Assert.Empty(actions);
    }

    // -------------------------------------------------------------------------
    // Linked-pair behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_ReturnsNoAction_WhenLinkedPairUnchanged()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(),
            [CreateItem("a1", "v1")],
            [CreateItem("b1", "v1")],
            [link]);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_UpdatesB_WhenOnlySideAChanged()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v1")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
        Assert.Equal(SyncSide.B, actions[0].TargetSide);
    }

    [Fact]
    public void PlanActions_UpdatesA_WhenOnlySideBChanged()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(),
            [CreateItem("a1", "v1")],
            [CreateItem("b1", "v2")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
        Assert.Equal(SyncSide.A, actions[0].TargetSide);
    }

    [Fact]
    public void PlanActions_UpdatesB_WhenBothChangedAndSideAWins()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(conflictPolicy: ConflictPolicy.SideAWins),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v2b")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
        Assert.Equal(SyncSide.B, actions[0].TargetSide);
        Assert.Equal("a1", actions[0].Item!.SourceId);
    }

    [Fact]
    public void PlanActions_UpdatesA_WhenBothChangedAndSideBWins()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(conflictPolicy: ConflictPolicy.SideBWins),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v2b")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Update, actions[0].Kind);
        Assert.Equal(SyncSide.A, actions[0].TargetSide);
        Assert.Equal("b1", actions[0].Item!.SourceId);
    }

    [Fact]
    public void PlanActions_ReturnsNoOp_WhenBothChangedAndPolicyIsSkip()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(conflictPolicy: ConflictPolicy.Skip),
            [CreateItem("a1", "v2")],
            [CreateItem("b1", "v2b")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.NoOp, actions[0].Kind);
    }

    [Fact]
    public void PlanActions_RespectsAtoBDirectionForLinkedPair()
    {
        var link = CreateLink("a1", "b1", sideAVersion: "v1", sideBVersion: "v1");
        var changedItem = CreateItem("a1", "v2");

        // AToB mode: A changed → plan action
        Assert.Single(planner.PlanActions(CreateJob(SyncMode.AToB), [changedItem], [CreateItem("b1", "v1")], [link]));
        // BToA mode: A changed only → no action
        Assert.Empty(planner.PlanActions(CreateJob(SyncMode.BToA), [changedItem], [CreateItem("b1", "v1")], [link]));
    }

    // -------------------------------------------------------------------------
    // Duplicate / incremental scenarios
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_CreatesOnB_WhenLinkedBIsNotEligibleAndNewAMatchesIt()
    {
        // a1 is already linked to b1.
        // a2 appears and semantically matches b1 (same display name + email).
        // b1 must NOT be a duplicate candidate for a2; a2 should result in Create(B).
        var link = CreateLink("a1", "b1");
        var a2 = CreateContactItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateContactItem("b1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.AToB),
            [a2],
            [b1],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Create, actions[0].Kind);
        Assert.Equal(SyncSide.B, actions[0].TargetSide);
        Assert.Equal("a2", actions[0].Item!.SourceId);
    }

    [Fact]
    public void PlanActions_CreatesOnA_WhenLinkedAIsNotEligibleAndNewBMatchesIt()
    {
        // Mirror of the scenario above: b1 is linked to a1; b2 matches a1 semantically.
        // a1 must NOT be a duplicate candidate for b2; b2 should result in Create(A).
        var link = CreateLink("a1", "b1");
        var b2 = CreateContactItem("b2", displayName: "Alice", email: "alice@example.com");
        var a1 = CreateContactItem("a1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.BToA),
            [a1],
            [b2],
            [link]);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.Create, actions[0].Kind);
        Assert.Equal(SyncSide.A, actions[0].TargetSide);
        Assert.Equal("b2", actions[0].Item!.SourceId);
    }

    [Fact]
    public void PlanActions_ReturnsNoOp_WhenOneSourceMatchesMultipleTargets()
    {
        // a1 matches both b1 and b2 (ambiguous initial duplicate).
        var a1 = CreateContactItem("a1", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateContactItem("b1", displayName: "Alice", email: "alice@example.com");
        var b2 = CreateContactItem("b2", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.AToB),
            [a1],
            [b1, b2],
            []);

        Assert.Single(actions);
        Assert.Equal(SyncActionKind.NoOp, actions[0].Kind);
    }

    [Fact]
    public void PlanActions_ReturnsNoOp_WhenOneTargetMatchesMultipleSources()
    {
        // Mirror ambiguity: b1 matches both a1 and a2.
        var a1 = CreateContactItem("a1", displayName: "Alice", email: "alice@example.com");
        var a2 = CreateContactItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateContactItem("b1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.BToA),
            [a1, a2],
            [b1],
            []);

        // Both b→a actions must be NoOp (ambiguous competition).
        Assert.All(actions, a => Assert.Equal(SyncActionKind.NoOp, a.Kind));
    }

    [Fact]
    public void PlanActions_NoOpForAllCompetitors_WhenMultipleSourcesCompeteForSameTarget()
    {
        // a1 and a2 both uniquely match b1 (many-to-one competition).
        var a1 = CreateContactItem("a1", displayName: "Alice", email: "alice@example.com");
        var a2 = CreateContactItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateContactItem("b1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.AToB),
            [a1, a2],
            [b1],
            []);

        // Both a1 and a2 must be NoOp — no random winner.
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(SyncActionKind.NoOp, a.Kind));
    }

    // -------------------------------------------------------------------------
    // Invariants: no two actions share the same source or target item
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_NeverProducesTwoActionsWithSameSourceItem()
    {
        // Build a scenario with multiple unlinked items to exercise a variety of paths.
        var sideA = new[]
        {
            CreateItem("a1", "v1"),
            CreateItem("a2", "v2"),
            CreateItem("a3", "v3"),
        };
        var sideB = new[]
        {
            CreateItem("b1", "v1"),
        };
        var links = new[] { CreateLink("a1", "b1") };

        IReadOnlyList<SyncAction> actions = planner.PlanActions(CreateJob(), sideA, sideB, links);

        var sourceIds = actions
            .Where(a => a.Item is not null)
            .Select(a => a.Item!.SourceId)
            .ToList();

        Assert.Equal(sourceIds.Count, sourceIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlanActions_NeverProducesTwoActionsWithSameTargetItem()
    {
        // Two unlinked contacts both matching the same target (many-to-one case).
        var a1 = CreateContactItem("a1", displayName: "Alice", email: "alice@example.com");
        var a2 = CreateContactItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateContactItem("b1", displayName: "Alice", email: "alice@example.com");

        IReadOnlyList<SyncAction> actions = planner.PlanActions(
            CreateJob(SyncMode.AToB),
            [a1, a2],
            [b1],
            []);

        // Collect target IDs from Update/Create actions.
        var targetIds = actions
            .Where(a => a.Kind is SyncActionKind.Update && a.MatchedTargetItem is not null)
            .Select(a => a.MatchedTargetItem!.SourceId)
            .ToList();

        Assert.Equal(targetIds.Count, targetIds.Distinct(StringComparer.Ordinal).Count());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    private static CanonicalItem CreateContactItem(
        string id,
        string version = "v1",
        string displayName = "",
        string email = "",
        DateTimeOffset? lastModified = null) =>
        new()
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Version = version,
            Payload = new CanonicalContact
            {
                DisplayName = displayName,
                Emails = string.IsNullOrEmpty(email) ? [] : [new ContactEmail { Address = email }],
                LastModified = lastModified,
            },
        };

    private static LinkStateRow CreateLink(
        string sideAId,
        string sideBId,
        string? sideAVersion = null,
        string? sideBVersion = null) =>
        new()
        {
            JobKey = "job-1",
            EntityType = EntityType.CalendarEvent,
            SideAId = sideAId,
            SideBId = sideBId,
            SideAVersion = sideAVersion,
            SideBVersion = sideBVersion,
        };
}
