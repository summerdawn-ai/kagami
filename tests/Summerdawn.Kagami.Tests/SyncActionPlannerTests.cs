using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Configuration.DeletePolicy;
using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

namespace Summerdawn.Kagami.Tests;

public sealed class SyncActionPlannerTests
{
    private readonly SyncActionPlanner planner = new(new LinkCreator(NullLogger<LinkCreator>.Instance));

    // -------------------------------------------------------------------------
    // Directional mode / basic create behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_ReturnsEmpty_WhenModeIsReverseAndOnlySourceSideHasItems()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.Reverse), [CreateItem("a1")], [], []);
        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_ReturnsEmpty_WhenModeIsForwardAndOnlyDestinationSideHasItems()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.Forward), [], [CreateItem("b1")], []);
        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_CreatesOnDestination_WhenUnlinkedSourceItemInForwardMode()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.Forward), [CreateItem("a1")], [], []);

        Assert.Single(actions);
        Assert.Equal(Create, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
    }

    [Fact]
    public void PlanActions_CreatesOnSource_WhenUnlinkedDestinationItemInReverseMode()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.Reverse), [], [CreateItem("b1")], []);

        Assert.Single(actions);
        Assert.Equal(Create, actions[0].Kind);
        Assert.Equal(DestinationToSource, actions[0].Direction);
    }

    [Fact]
    public void PlanActions_CreatesBothDirections_WhenBidirectionalAndBothUnlinked()
    {
        var actions = planner.PlanActions(CreateJob(SyncMode.Bidirectional), [CreateItem("a1", displayName: "foo")], [CreateItem("b1", displayName: "bar")], []);

        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, a => a is { Kind: Create, Direction: SourceToDestination });
        Assert.Contains(actions, a => a is { Kind: Create, Direction: DestinationToSource });
    }

    // -------------------------------------------------------------------------
    // Delete behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_DeletesLinkedTarget_WhenSourceDeletedAndMirrorEnabled()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(deletePolicy: Mirror),
            [new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } }],
            [CreateItem("b1")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Delete, actions[0].Kind);
        Assert.Equal("b1", actions[0].Link.DestinationId);
    }

    [Fact]
    public void PlanActions_SkipsDelete_WhenDeletePolicyIgnore()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(deletePolicy: Ignore),
            [new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } }],
            [CreateItem("b1")],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
    }

    // -------------------------------------------------------------------------
    // Linked-pair behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_ReturnsNoAction_WhenLinkedPairUnchanged()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(),
            [CreateItem("a1", "v1")],
            [CreateItem("b1", "v1")],
            [link]);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_UpdatesDestination_WhenOnlySourceChanged()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(),
            [CreateItem("a1", "v2") with { Notes = "foo" }],
            [CreateItem("b1", "v1") with { Notes = "bar" }],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Update, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
    }

    [Fact]
    public void PlanActions_UpdatesSource_WhenOnlyDestinationChanged()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(),
            [CreateItem("a1", "v1") with { Notes = "foo" }],
            [CreateItem("b1", "v2") with { Notes = "bar" }],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Update, actions[0].Kind);
        Assert.Equal(DestinationToSource, actions[0].Direction);
    }

    [Fact]
    public void PlanActions_UpdatesDestination_WhenBothChangedAndSourceWins()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(conflictPolicy: ConflictPolicy.SourceWins),
            [CreateItem("a1", "v2") with { Notes = "foo" }],
            [CreateItem("b1", "v2b") with { Notes = "bar" }],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Update, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
        Assert.Equal("a1", actions[0].Link.SourceId);
    }

    [Fact]
    public void PlanActions_UpdatesSource_WhenBothChangedAndDestinationWins()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(conflictPolicy: ConflictPolicy.DestinationWins),
            [CreateItem("a1", "v2") with { Notes = "foo" }],
            [CreateItem("b1", "v2b") with { Notes = "bar" }],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Update, actions[0].Kind);
        Assert.Equal(DestinationToSource, actions[0].Direction);
        Assert.Equal("b1", actions[0].Link.DestinationId);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenBothChangedAndPolicyIsSkip()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(conflictPolicy: ConflictPolicy.Skip),
            [CreateItem("a1", "v2") with { Notes = "source note" }],
            [CreateItem("b1", "v2b") with { Notes = "destination note" }],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenBothChangedAndLastWriteWinsPrefersSourceInReverseMode()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");
        var sourceItem = CreateItem("a1", version: "v3", lastModified: DateTimeOffset.Parse("2026-01-02T00:00:00Z")) with { Notes = "source" };
        var destinationItem = CreateItem("b1", version: "v2", lastModified: DateTimeOffset.Parse("2026-01-01T00:00:00Z")) with { Notes = "destination" };

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Reverse),
            [sourceItem],
            [destinationItem],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
        Assert.Equal(DestinationToSource, actions[0].Direction);
    }

    [Fact]
    public void PlanActions_UpdatesSource_WhenBothChangedAndLastWriteWinsPrefersDestinationInReverseMode()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");
        var sourceItem = CreateItem("a1", version: "v2", lastModified: DateTimeOffset.Parse("2026-01-01T00:00:00Z")) with { Notes = "source" };
        var destinationItem = CreateItem("b1", version: "v3", lastModified: DateTimeOffset.Parse("2026-01-02T00:00:00Z")) with { Notes = "destination" };

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Reverse),
            [sourceItem],
            [destinationItem],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Update, actions[0].Kind);
        Assert.Equal(DestinationToSource, actions[0].Direction);
        Assert.Equal("b1", actions[0].Link.DestinationId);
        Assert.Equal("a1", actions[0].Link.SourceId);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenBothChangedAndLastWriteWinsPrefersDestinationInForwardMode()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");
        var sourceItem = CreateItem("a1", version: "v2", lastModified: DateTimeOffset.Parse("2026-01-01T00:00:00Z")) with { Notes = "source" };
        var destinationItem = CreateItem("b1", version: "v3", lastModified: DateTimeOffset.Parse("2026-01-02T00:00:00Z")) with { Notes = "destination" };

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward),
            [sourceItem],
            [destinationItem],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
    }

    [Fact]
    public void PlanActions_RespectsForwardDirectionForLinkedPair()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");
        var changedItem = CreateItem("a1", "v2") with { Notes = "foo" };

        // Forward mode: A changed → plan action
        Assert.Single(planner.PlanActions(CreateJob(SyncMode.Forward), [changedItem], [CreateItem("b1", "v1") with { Notes = "bar" }], [link]));
        // Reverse mode: A changed only → no action
        Assert.Empty(planner.PlanActions(CreateJob(SyncMode.Reverse), [changedItem], [CreateItem("b1", "v1") with { Notes = "bar" }], [link]));
    }

    // -------------------------------------------------------------------------
    // Duplicate / incremental scenarios
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_CreatesOnDestination_WhenLinkedBIsNotEligibleAndNewAMatchesIt()
    {
        // a1 is already linked to b1 (both present, unchanged).
        // a2 appears and semantically matches b1 (same display name + email).
        // b1 must NOT be a duplicate candidate for a2; a2 should result in Create(Destination).
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1", entityType: EntityType.Contact);
        var a1 = CreateItem("a1", version: "v1"); // linked, unchanged → no action
        var a2 = CreateItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateItem("b1", version: "v1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward),
            [a1, a2],
            [b1],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Create, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
        Assert.Equal("a2", actions[0].Link.SourceId);
    }

    [Fact]
    public void PlanActions_CreatesOnSource_WhenLinkedAIsNotEligibleAndNewBMatchesIt()
    {
        // b1 is linked to a1 (both present, unchanged); b2 matches a1 semantically.
        // a1 must NOT be a duplicate candidate for b2; b2 should result in Create(Source).
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1", entityType: EntityType.Contact);
        var a1 = CreateItem("a1", version: "v1", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateItem("b1", version: "v1"); // linked, unchanged → no action
        var b2 = CreateItem("b2", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Reverse),
            [a1],
            [b1, b2],
            [link]);

        Assert.Single(actions);
        Assert.Equal(Create, actions[0].Kind);
        Assert.Equal(DestinationToSource, actions[0].Direction);
        Assert.Equal("b2", actions[0].Link.DestinationId);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenOneSourceMatchesMultipleTargets()
    {
        // a1 matches both b1 and b2 (ambiguous initial duplicate).
        var a1 = CreateItem("a1", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateItem("b1", displayName: "Alice", email: "alice@example.com");
        var b2 = CreateItem("b2", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward),
            [a1],
            [b1, b2],
            []);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenOneTargetMatchesMultipleSources()
    {
        // Mirror ambiguity: b1 matches both a1 and a2.
        var a1 = CreateItem("a1", displayName: "Alice", email: "alice@example.com");
        var a2 = CreateItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateItem("b1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Reverse),
            [a1, a2],
            [b1],
            []);

        // b1 has multiple A candidates → Skip for b1 (1 action, ambiguous).
        Assert.Single(actions);
        Assert.All(actions, a => Assert.Equal(Skip, a.Kind));
    }

    [Fact]
    public void PlanActions_SkipForAllCompetitors_WhenMultipleSourcesCompeteForSameTarget()
    {
        // a1 and a2 both uniquely match b1 (many-to-one competition).
        var a1 = CreateItem("a1", displayName: "Alice", email: "alice@example.com");
        var a2 = CreateItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateItem("b1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward),
            [a1, a2],
            [b1],
            []);

        // Both a1 and a2 must be Skip — no random winner.
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(Skip, a.Kind));
    }

    // -------------------------------------------------------------------------
    // Invariants: no two actions share the same source or target item
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_NeverProducesTwoActionsWithSameSourceItem()
    {
        // Build a scenario with multiple unlinked items to exercise a variety of paths.
        var sourceItems = new[]
        {
            CreateItem("a1", "v1"),
            CreateItem("a2", "v2"),
            CreateItem("a3", "v3"),
        };
        var destinationItems = new[]
        {
            CreateItem("b1", "v1"),
        };
        var links = new[] { CreateLink("a1", "b1") };

        var actions = planner.PlanActions(CreateJob(), sourceItems, destinationItems, links);

        var sourceIds = actions
            .Select(GetWriteItemId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        Assert.Equal(sourceIds.Count, sourceIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlanActions_NeverProducesTwoActionsWithSameTargetItem()
    {
        // Two unlinked contacts both matching the same target (many-to-one case).
        var a1 = CreateItem("a1", displayName: "Alice", email: "alice@example.com");
        var a2 = CreateItem("a2", displayName: "Alice", email: "alice@example.com");
        var b1 = CreateItem("b1", displayName: "Alice", email: "alice@example.com");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward),
            [a1, a2],
            [b1],
            []);

        // Collect target IDs from Update/Create actions.
        var targetIds = actions
            .Where(a => a.Kind is Update)
            .Select(GetMatchedTargetItemId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        Assert.Equal(targetIds.Count, targetIds.Distinct(StringComparer.Ordinal).Count());
    }

    // -------------------------------------------------------------------------
    // Absent-item and filter-scope deletion scenarios
    // -------------------------------------------------------------------------

    [Fact]
    public void PlanActions_DeletesLinkedDestination_WhenSourceAbsentFromFullScan()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, deletePolicy: Mirror, full: true),
            sourceItems: [],
            destinationItems: [CreateItem("b1")],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Delete, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
        Assert.Equal("b1", actions[0].Link.DestinationId);
    }

    [Fact]
    public void PlanActions_CreatesOnDestination_WhenDestinationAbsentAndForce()
    {
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, force: true),
            sourceItems: [CreateItem("a1", "v1")],
            destinationItems: [],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Create, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
        Assert.Equal("a1", actions[0].Link.SourceId);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenSourceAbsentAndDeletePolicyIsIgnore()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, deletePolicy: Ignore, full: true),
            sourceItems: [],
            destinationItems: [CreateItem("b1")],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
        Assert.Contains("Ignore", actions[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenSourceAbsentAndSyncModeIsReverse()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Reverse, deletePolicy: Mirror, full: true),
            sourceItems: [],
            destinationItems: [CreateItem("b1")],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenDestinationAbsentAndDeletePolicyIsIgnore()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Bidirectional, deletePolicy: Ignore, full: true),
            sourceItems: [CreateItem("a1")],
            destinationItems: [],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
        Assert.Contains("Ignore", actions[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanActions_ReturnsSkip_WhenDestinationAbsentAndSyncModeIsForward()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, deletePolicy: Mirror, full: true),
            sourceItems: [CreateItem("a1")],
            destinationItems: [],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Skip, actions[0].Kind);
    }

    [Fact]
    public void PlanActions_ReturnsEmpty_WhenBothSidesAbsent()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions<CanonicalContact>(
            CreateJob(SyncMode.Bidirectional, deletePolicy: Mirror),
            sourceItems: [],
            destinationItems: [],
            existingLinks: [link]);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_DeletesLinkedDestination_WhenSourceItemFilteredOutOfScope()
    {
        // Source item a1 is PRESENT in the raw delta but does NOT pass the active filter
        // (it changed category and left scope).  The persisted link row exists, so the
        // examiner marks it as MovedOutOfScope → planner should delete the mirrored copy.
        var link = CreateLink("a1", "b1");
        var outOfScopeSource = CreateItem("a1") with { DisplayName = "Other" }; // fails "startswith(name,'A')" filter
        var filter = ContactFilter.Parse("startswith(name,'A')");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Bidirectional, deletePolicy: Mirror),
            sourceItems: [outOfScopeSource],
            destinationItems: [CreateItem("b1")],
            existingLinks: [link],
            filter: filter);

        Assert.Single(actions);
        Assert.Equal(Delete, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
        Assert.Equal("b1", actions[0].Link.DestinationId);
    }

    [Fact]
    public void PlanActions_NoAction_WhenSourceItemHasNoLinkAndFailsFilter()
    {
        // Source item fails filter and has NO prior link row — it was never part of the synced
        // set.  The planner must produce no action and no skip to avoid noisy logs.
        var outOfScopeSource = CreateItem("a1") with { DisplayName = "Other" }; // fails filter
        var filter = ContactFilter.Parse("startswith(name,'A')");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, deletePolicy: Mirror),
            sourceItems: [outOfScopeSource],
            destinationItems: [],
            existingLinks: [],
            filter: filter);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_NoAction_WhenLinkedSourceAbsentFromDeltaRun()
    {
        // On a cursor-based (delta) run, absence of the source item means it did not change —
        // it is implicitly still present.  The planner must produce no action.
        var link = CreateLink("a1", "b1", sourceVersion: "v1", destinationVersion: "v1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, deletePolicy: Mirror, full: false), // delta run
            sourceItems: [],                     // source absent from delta
            destinationItems: [CreateItem("b1", "v1")],
            existingLinks: [link]);

        Assert.Empty(actions);
    }

    [Fact]
    public void PlanActions_DeleteAction_DoesNotExposePersistedIdAsDestinationId_WhenDestinationItemNotLoaded()
    {
        var link = CreateLink("a1", "b1");

        var actions = planner.PlanActions(
            CreateJob(SyncMode.Forward, deletePolicy: Mirror, full: false),
            sourceItems: [new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } }],
            destinationItems: [],
            existingLinks: [link]);

        Assert.Single(actions);
        Assert.Equal(Delete, actions[0].Kind);
        Assert.Equal(SourceToDestination, actions[0].Direction);
        Assert.Null(actions[0].Link.DestinationId);
        Assert.Equal("b1", actions[0].Link.PersistedState!.DestinationId);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static JobOptions CreateJob(
        SyncMode mode = SyncMode.Bidirectional,
        DeletePolicy deletePolicy = Mirror,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        bool force = false,
        bool full = false) =>
        new()
        {
            Enabled = true,
            EntityType = EntityType.CalendarEvent,
            SourceEndpointName = "endpointA",
            DestinationEndpointName = "endpointB",
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
            Force = force,
            Full = full,
        };

    private static CanonicalContact CreateItem(
        string id,
        string version = "v1",
        string displayName = "",
        string email = "",
        DateTimeOffset? lastModified = null) => new()
        {
            DisplayName = displayName,
            Emails = string.IsNullOrEmpty(email) ? [] : [new ContactEmail { Address = email }],

            Provenance =
            {
                ProviderId = id,
                Version = version,
                LastModified = lastModified,
            }
        };

    private static LinkStateRow CreateLink(
        string sourceId,
        string destinationId,
        string? sourceVersion = null,
        string? destinationVersion = null,
        string entityType = EntityType.CalendarEvent) =>
        new()
        {
            PartitionKey = $"{entityType}:endpointA:endpointB",
            SourceId = sourceId,
            DestinationId = destinationId,
            SourceVersion = sourceVersion,
            DestinationVersion = destinationVersion,
        };

    private static string? GetWriteItemId(SyncAction<CanonicalContact> action) =>
        action.Direction == SourceToDestination
            ? action.Link.SourceItem?.Provenance.ProviderId
            : action.Link.DestinationItem?.Provenance.ProviderId;

    private static string? GetMatchedTargetItemId(SyncAction<CanonicalContact> action) =>
        action.Direction == SourceToDestination
            ? action.Link.DestinationItem?.Provenance.ProviderId
            : action.Link.SourceItem?.Provenance.ProviderId;
}
