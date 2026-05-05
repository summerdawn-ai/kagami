using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Tests;

public sealed class LinkCreatorTests
{
    private readonly LinkCreator creator = new(NullLogger<LinkCreator>.Instance);

    // -------------------------------------------------------------------------
    // Persisted links
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildLinks_CreatesPersisted_WhenLinkRowExistsAndItemsPresent()
    {
        var source = Item("a1", "Alice");
        var dest = Item("b1", "Alice");
        var row = Row("a1", "b1");

        var links = creator.BuildLinks([source], [dest], [row]);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Persisted, link.Kind);
        Assert.Same(source, link.SourceItem);
        Assert.Same(dest, link.DestinationItem);
        Assert.Same(row, link.PersistedState);
    }

    [Fact]
    public void BuildLinks_CreatesPersisted_WhenSourceDeletedButLinkRowExists()
    {
        var deletedSource = new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } };
        var dest = Item("b1", "Alice");
        var row = Row("a1", "b1");

        var links = creator.BuildLinks([deletedSource], [dest], [row]);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Persisted, link.Kind);
        Assert.Same(deletedSource, link.SourceItem);
        Assert.Same(dest, link.DestinationItem);
    }

    [Fact]
    public void BuildLinks_CreatesPersisted_WithNullSource_WhenSourceNotInLoadedItems()
    {
        // Source item not in loaded list (outside filter scope, effectively absent)
        var dest = Item("b1", "Alice");
        var row = Row("a1", "b1");

        var links = creator.BuildLinks([], [dest], [row]);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Persisted, link.Kind);
        Assert.Null(link.SourceItem);
        Assert.Same(dest, link.DestinationItem);
    }

    [Fact]
    public void BuildLinks_CreatesPersisted_WithNullDest_WhenDestinationNotInLoadedItems()
    {
        var source = Item("a1", "Alice");
        var row = Row("a1", "b1");

        var links = creator.BuildLinks([source], [], [row]);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Persisted, link.Kind);
        Assert.Same(source, link.SourceItem);
        Assert.Null(link.DestinationItem);
    }

    [Fact]
    public void BuildLinks_SkipsLinkRow_WhenNeitherSideInLoadedItems()
    {
        // This link row is "untouched" — neither side appears in the loaded sets.
        var row = Row("a1", "b1");

        var links = creator.BuildLinks<CanonicalContact>([], [], [row]);

        Assert.Empty(links);
    }

    [Fact]
    public void BuildLinks_IncludesLinkRow_WhenOnlyDestinationSideIsLoaded()
    {
        // Source not loaded; destination IS loaded → link is relevant via DestinationId.
        var dest = Item("b1", "Alice");
        var row = Row("a1", "b1");

        var links = creator.BuildLinks([], [dest], [row]);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Persisted, link.Kind);
        Assert.Null(link.SourceItem);
        Assert.Same(dest, link.DestinationItem);
    }

    // -------------------------------------------------------------------------
    // Inferred links (new matches, no prior link row)
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildLinks_CreatesInferred_WhenUnlinkedItemsMatchByName()
    {
        var source = Item("a1", "Alice Smith", email: "alice@example.com");
        var dest = Item("b1", "Alice Smith", email: "alice@example.com");

        var links = creator.BuildLinks([source], [dest], []);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Inferred, link.Kind);
        Assert.Same(source, link.SourceItem);
        Assert.Same(dest, link.DestinationItem);
        Assert.Null(link.PersistedState);
    }

    [Fact]
    public void BuildLinks_CreatesInferred_WhenUnique1To1NameMatch()
    {
        // Unique name match (no identifiers required for 1:1)
        var source = Item("a1", "Unique Person");
        var dest = Item("b1", "Unique Person");

        var links = creator.BuildLinks([source], [dest], []);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Inferred, link.Kind);
    }

    // -------------------------------------------------------------------------
    // Unmatched links
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildLinks_CreatesUnmatchedSource_WhenNoTargetFound()
    {
        var source = Item("a1", "Alice");

        var links = creator.BuildLinks([source], [], []);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Unmatched, link.Kind);
        Assert.Same(source, link.SourceItem);
        Assert.Null(link.DestinationItem);
    }

    [Fact]
    public void BuildLinks_CreatesUnmatchedDest_WhenNoSourceMatchFound()
    {
        var dest = Item("b1", "Bob");

        var links = creator.BuildLinks([], [dest], []);

        var link = Assert.Single(links);
        Assert.Equal(LinkKind.Unmatched, link.Kind);
        Assert.Null(link.SourceItem);
        Assert.Same(dest, link.DestinationItem);
    }

    [Fact]
    public void BuildLinks_SkipsDeletedItems_ForUnmatchedPasses()
    {
        // Deleted items are not eligible for new matching
        var deleted = new CanonicalContact { IsDeleted = true, Provenance = { ProviderId = "a1" } };

        var links = creator.BuildLinks<CanonicalContact>([deleted], [], []);

        Assert.Empty(links);
    }

    // -------------------------------------------------------------------------
    // Ambiguous links
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildLinks_CreatesAmbiguous_WhenSourceMatchesMultipleTargets()
    {
        // One source, two targets with the same name + matching identifier
        var source = Item("a1", "Alice Smith", email: "alice@example.com");
        var dest1 = Item("b1", "Alice Smith", email: "alice@example.com");
        var dest2 = Item("b2", "Alice Smith", email: "alice@example.com");

        var links = creator.BuildLinks([source], [dest1, dest2], []);

        // source→Ambiguous (can't decide which target); each destination item→Unmatched
        Assert.Equal(3, links.Count);
        var ambiguous = links.Single(l => l.Kind == LinkKind.Ambiguous);
        Assert.Same(source, ambiguous.SourceItem);
        Assert.Equal(2, links.Count(l => l.Kind == LinkKind.Unmatched));
    }

    [Fact]
    public void BuildLinks_CreatesAmbiguous_WhenMultipleSourcesCompeteForSameTarget()
    {
        // Two sources that uniquely name-match to the same target
        var source1 = Item("a1", "Alice Smith", email: "alice@example.com");
        var source2 = Item("a2", "Alice Smith", email: "alice@example.com");
        var dest = Item("b1", "Alice Smith", email: "alice@example.com");

        var links = creator.BuildLinks([source1, source2], [dest], []);

        // Each source is source-side Ambiguous; the contested target is dest-side Ambiguous.
        Assert.Equal(3, links.Count);
        Assert.Equal(2, links.Count(l => l.Kind == LinkKind.Ambiguous && l.SourceItem != null));
        var contested = links.Single(l => l.Kind == LinkKind.Ambiguous && l.SourceItem == null);
        Assert.Same(dest, contested.DestinationItem);
    }

    // -------------------------------------------------------------------------
    // Reservation – persisted items excluded from unlinked passes
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildLinks_DoesNotMatchLinkedItemsAgain()
    {
        // a1↔b1 is persisted; b1 should not be matched to a2
        var source1 = Item("a1", "Alice");
        var source2 = Item("a2", "Alice");
        var dest = Item("b1", "Alice");
        var row = Row("a1", "b1");

        var links = creator.BuildLinks([source1, source2], [dest], [row]);

        // a1↔b1: persisted; a2: unmatched (b1 reserved)
        Assert.Equal(2, links.Count);
        var persisted = links.Single(l => l.Kind == LinkKind.Persisted);
        var unmatched = links.Single(l => l.Kind == LinkKind.Unmatched);
        Assert.Equal("a1", persisted.SourceItem!.Provenance.ProviderId);
        Assert.Equal("a2", unmatched.SourceItem!.Provenance.ProviderId);
    }

    // -------------------------------------------------------------------------
    // Every observed item in exactly one link
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildLinks_EveryItemAppearsInExactlyOneLink()
    {
        var s1 = Item("a1", "Alice", email: "alice@a.com");
        var s2 = Item("a2", "Bob");
        var d1 = Item("b1", "Alice", email: "alice@a.com");
        var d2 = Item("b2", "Charlie");
        var row = Row("a2", "b3"); // b3 not in loaded dest — row still relevant via source

        var links = creator.BuildLinks([s1, s2], [d1, d2], [row]);

        // Verify every non-deleted item in source and dest appears exactly once
        var sourceIds = links.Where(l => l.SourceItem != null).Select(l => l.SourceItem!.Provenance.ProviderId).ToList();
        var destIds = links.Where(l => l.DestinationItem != null).Select(l => l.DestinationItem!.Provenance.ProviderId).ToList();

        Assert.Contains("a1", sourceIds);
        Assert.Contains("a2", sourceIds);
        Assert.Contains("b1", destIds);
        Assert.Contains("b2", destIds);

        // No duplicates
        Assert.Equal(sourceIds.Distinct().Count(), sourceIds.Count);
        Assert.Equal(destIds.Distinct().Count(), destIds.Count);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CanonicalContact Item(string id, string displayName = "", string? email = null)
    {
        var contact = new CanonicalContact
        {
            DisplayName = displayName,
            Provenance = { ProviderId = id },
        };
        if (email is not null)
        {
            contact.Emails.Add(new ContactEmail { Address = email });
        }

        return contact;
    }

    private static LinkStateRow Row(string sourceId, string? destinationId) =>
        new() { SourceId = sourceId, DestinationId = destinationId };
}
