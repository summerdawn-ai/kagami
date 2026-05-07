using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Tests;

public sealed class LinkExaminerTests
{
    // -------------------------------------------------------------------------
    // Persisted links – source-side activity
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_SourceUnchanged_WhenVersionMatchesPersistedLink()
    {
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v1"),
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Unchanged, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceModified_WhenVersionDiffersFromPersistedLink()
    {
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v2"),
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Modified, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceDeleted_WhenSourceItemIsDeleted()
    {
        var link = PersistedLink(
            sourceItem: new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } },
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Deleted, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceDeleted_WhenSourceItemNull()
    {
        var link = PersistedLink(
            sourceItem: null,
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Deleted, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceUnchanged_WhenHashMatchesAndNoVersion()
    {
        var item = new CanonicalContact { Provenance = { ProviderId = "a1", ContentHash = "abc123" } };
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Persisted,
            SourceItem = item,
            DestinationItem = Item("b1"),
            PersistedState = new LinkStateRow { SourceId = "a1", DestinationId = "b1", SourceHash = "abc123" },
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Unchanged, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceModified_WhenHashDiffersAndNoVersion()
    {
        var item = new CanonicalContact { Provenance = { ProviderId = "a1", ContentHash = "newhash" } };
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Persisted,
            SourceItem = item,
            DestinationItem = Item("b1"),
            PersistedState = new LinkStateRow { SourceId = "a1", DestinationId = "b1", SourceHash = "oldhash" },
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Modified, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceModified_WhenNeitherVersionNorHashAvailable()
    {
        // No version or hash info → conservatively assume changed
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Persisted,
            SourceItem = Item("a1"),
            DestinationItem = Item("b1"),
            PersistedState = new LinkStateRow { SourceId = "a1", DestinationId = "b1" },
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Modified, result.SourceActivity);
    }

    // -------------------------------------------------------------------------
    // Persisted links – destination-side activity
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_DestinationUnchanged_WhenVersionMatchesPersistedLink()
    {
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v1"),
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Unchanged, result.DestinationActivity);
    }

    [Fact]
    public void Examine_DestinationModified_WhenVersionDiffersFromPersistedLink()
    {
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v1"),
            destinationItem: Item("b1", version: "v2"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Modified, result.DestinationActivity);
    }

    [Fact]
    public void Examine_DestinationDeleted_WhenDestinationItemNull()
    {
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v1"),
            destinationItem: null,
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Deleted, result.DestinationActivity);
    }

    [Fact]
    public void Examine_DestinationAbsent_WhenPersistedDestinationIdIsNull()
    {
        // Link exists but destination was never created
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Persisted,
            SourceItem = Item("a1", version: "v1"),
            DestinationItem = null,
            PersistedState = new LinkStateRow { SourceId = "a1", DestinationId = null, SourceVersion = "v1" },
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Absent, result.DestinationActivity);
    }

    // -------------------------------------------------------------------------
    // Inferred links (no persisted state)
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_BothCreated_ForInferredLinkWithBothItemsPresent()
    {
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Inferred,
            SourceItem = Item("a1"),
            DestinationItem = Item("b1"),
            PersistedState = null,
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Created, result.SourceActivity);
        Assert.Equal(SideActivity.Created, result.DestinationActivity);
    }

    // -------------------------------------------------------------------------
    // Unmatched links (one side only)
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_SourceCreated_DestinationAbsent_ForSourceOnlyUnmatchedLink()
    {
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Unmatched,
            SourceItem = Item("a1"),
            DestinationItem = null,
            PersistedState = null,
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Created, result.SourceActivity);
        Assert.Equal(SideActivity.Absent, result.DestinationActivity);
    }

    [Fact]
    public void Examine_SourceAbsent_DestinationCreated_ForDestinationOnlyUnmatchedLink()
    {
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Unmatched,
            SourceItem = null,
            DestinationItem = Item("b1"),
            PersistedState = null,
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Absent, result.SourceActivity);
        Assert.Equal(SideActivity.Created, result.DestinationActivity);
    }

    // -------------------------------------------------------------------------
    // Ambiguous links
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_SourceCreated_DestinationAbsent_ForAmbiguousLinkWithNoCandidate()
    {
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Ambiguous,
            SourceItem = Item("a1"),
            DestinationItem = null,
            PersistedState = null,
        };

        var result = LinkExaminer.Examine(link);

        Assert.Equal(SideActivity.Created, result.SourceActivity);
        Assert.Equal(SideActivity.Absent, result.DestinationActivity);
    }

    // -------------------------------------------------------------------------
    // Delta-run semantics (absent from delta ≠ deleted)
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_SourceUnchanged_WhenSourceNullAndDeltaRun()
    {
        // On a delta run, absence of source means "did not change", not "deleted".
        var link = PersistedLink(
            sourceItem: null,
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link, DeltaJobOptions);

        Assert.Equal(SideActivity.Unchanged, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceDeleted_WhenSourceNullAndFullRun()
    {
        // On a full-scan run, absence of source means "deleted / gone from endpoint".
        var link = PersistedLink(
            sourceItem: null,
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link, FullJobOptions);

        Assert.Equal(SideActivity.Deleted, result.SourceActivity);
    }

    [Fact]
    public void Examine_SourceDeleted_WhenExplicitDeletionTombstoneAndDeltaRun()
    {
        // An explicit IsDeleted=true tombstone always means Deleted, even on a delta run.
        var link = PersistedLink(
            sourceItem: new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } },
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var result = LinkExaminer.Examine(link, DeltaJobOptions);

        Assert.Equal(SideActivity.Deleted, result.SourceActivity);
    }

    // -------------------------------------------------------------------------
    // Filter scope semantics (MovedOutOfScope and IsRelevantToCurrentScope)
    // -------------------------------------------------------------------------

    [Fact]
    public void Examine_SourceMovedOutOfScope_WhenItemFailsFilterAndLinkExists()
    {
        // a1 is visible in the current scan but fails the filter ("Alice" filter, a1 is "Bob").
        // A persisted link exists → activity should be MovedOutOfScope.
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v2", displayName: "Bob"),
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var result = LinkExaminer.Examine(link, FullJobOptions, filter);

        Assert.Equal(SideActivity.MovedOutOfScope, result.SourceActivity);
        Assert.True(result.IsRelevantToCurrentScope);
    }

    [Fact]
    public void Examine_IsNotRelevantToCurrentScope_WhenItemFailsFilterAndNoLinkExists()
    {
        // c1 "Charlie" fails the filter and has no persisted link row — it was never in scope.
        // The link should be marked as not relevant so the planner silently ignores it.
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Unmatched,
            SourceItem = Item("c1", version: "v1", displayName: "Charlie"),
            DestinationItem = null,
            PersistedState = null,
        };

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var result = LinkExaminer.Examine(link, FullJobOptions, filter);

        Assert.False(result.IsRelevantToCurrentScope);
    }

    [Fact]
    public void Examine_IsRelevantToCurrentScope_WhenItemPassesFilter()
    {
        var link = new Link<CanonicalContact>
        {
            Kind = LinkKind.Unmatched,
            SourceItem = Item("a1", version: "v1", displayName: "Alice"),
            DestinationItem = null,
            PersistedState = null,
        };

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var result = LinkExaminer.Examine(link, FullJobOptions, filter);

        Assert.True(result.IsRelevantToCurrentScope);
        Assert.Equal(SideActivity.Created, result.SourceActivity);
    }

    [Fact]
    public void Examine_IsRelevantToCurrentScope_WhenLinkExistsEvenIfItemFailsFilter()
    {
        // Even though "Bob" fails the filter, the link exists → relevant (MovedOutOfScope).
        var link = PersistedLink(
            sourceItem: Item("a1", version: "v2", displayName: "Bob"),
            destinationItem: Item("b1", version: "v1"),
            sourceVersion: "v1", destinationVersion: "v1");

        var filter = ContactFilter.Parse("startswith(name,'Alice')")!;
        var result = LinkExaminer.Examine(link, FullJobOptions, filter);

        Assert.True(result.IsRelevantToCurrentScope);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly JobOptions FullJobOptions = new()
    {
        Enabled = true,
        EntityType = "contact",
        SourceEndpointName = "endpointA",
        DestinationEndpointName = "endpointB",
        Full = true,
    };

    private static readonly JobOptions DeltaJobOptions = new()
    {
        Enabled = true,
        EntityType = "contact",
        SourceEndpointName = "endpointA",
        DestinationEndpointName = "endpointB",
        Full = false,
        Force = false,
    };

    private static CanonicalContact Item(string id, string? version = null, string? displayName = null) =>
        new() { DisplayName = displayName ?? string.Empty, Provenance = { ProviderId = id, Version = version } };

    private static Link<CanonicalContact> PersistedLink(
        CanonicalContact? sourceItem,
        CanonicalContact? destinationItem,
        string? sourceVersion,
        string? destinationVersion)
    {
        return new Link<CanonicalContact>
        {
            Kind = LinkKind.Persisted,
            SourceItem = sourceItem,
            DestinationItem = destinationItem,
            PersistedState = new LinkStateRow
            {
                SourceId = "a1",
                DestinationId = "b1",
                SourceVersion = sourceVersion,
                DestinationVersion = destinationVersion,
            },
        };
    }
}
