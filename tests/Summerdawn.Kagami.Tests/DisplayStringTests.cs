using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

using static Summerdawn.Kagami.Engine.LinkKind;
using static Summerdawn.Kagami.Engine.SideActivity;
using static Summerdawn.Kagami.Engine.SyncActionKind;
using static Summerdawn.Kagami.Engine.SyncDirection;

namespace Summerdawn.Kagami.Tests;

public sealed class DisplayStringTests
{
    [Fact]
    public void CanonicalContact_ToDisplayString_PrefersHumanReadableName()
    {
        var displayNameContact = new CanonicalContact
        {
            DisplayName = "John Doe",
            Provenance = { ProviderId = "contact-1" },
        };
        var organizationContact = new CanonicalContact
        {
            Organization = "Contoso Ltd",
            Provenance = { ProviderId = "contact-2" },
        };
        var identifierContact = new CanonicalContact
        {
            Provenance = { ProviderId = "contact-3" },
        };

        Assert.Equal("John Doe", displayNameContact.ToDisplayString());
        Assert.Equal("Contoso Ltd", organizationContact.ToDisplayString());
        Assert.Equal("contact-3", identifierContact.ToDisplayString());
    }

    [Fact]
    public void SyncAction_ToDisplayString_FormatsCreateUpdateDeleteAndSkip()
    {
        var sourceContact = CreateContact("a1", "John Doe", "Google");
        var targetContact = CreateContact("b1", "John Doe", "Microsoft");

        var createAction = CreateAction(Create, SourceToDestination, sourceContact, null);
        createAction.SourceEndpointName = "Google";
        createAction.DestinationEndpointName = "Microsoft";

        var updateAction = CreateAction(Update, SourceToDestination, sourceContact, targetContact);
        var deleteAction = CreateAction(Delete, SourceToDestination, sourceContact with { IsDeleted = true }, targetContact);
        var skipAction = CreateAction(Skip, SourceToDestination, sourceContact, targetContact, "Conflict: skipped per policy");

        Assert.Equal(
            "Create 'John Doe' on endpoint Microsoft (reason: New contact on endpoint Google)",
            createAction.ToDisplayString());
        Assert.Equal(
            "Update 'John Doe' on endpoint Microsoft (reason: Contact updated on endpoint Google)",
            updateAction.ToDisplayString());
        Assert.Equal(
            "Delete 'John Doe' on endpoint Microsoft (reason: Contact deleted on endpoint Google)",
            deleteAction.ToDisplayString());
        Assert.Equal(
            "Skip 'John Doe' on endpoint Microsoft (reason: Conflict: skipped per policy)",
            skipAction.ToDisplayString());
    }

    [Fact]
    public void SyncAction_ToDisplayString_FallsBackToPersistedStateWhenItemsAreUnavailable()
    {
        var sourceContact = CreateContact("a1", "John Doe", "Google");
        var action = CreateAction(
            Delete,
            SourceToDestination,
            sourceContact with { IsDeleted = true },
            null,
            persistedState: new LinkStateRow
            {
                SourceId = "a1",
                DestinationId = "b1",
            });
        action.SourceEndpointName = "Google";
        action.DestinationEndpointName = "Microsoft";

        Assert.Equal(
            "Delete 'b1' on endpoint Microsoft (reason: Contact deleted on endpoint Google)",
            action.ToDisplayString());
    }

    private static SyncAction<CanonicalContact> CreateAction(
        SyncActionKind kind,
        SyncDirection direction,
        CanonicalContact? sourceItem,
        CanonicalContact? destinationItem,
        string? reason = null,
        LinkStateRow? persistedState = null) =>
        new()
        {
            Kind = kind,
            Direction = direction,
            Reason = reason,
            Link = new ExaminedLink<CanonicalContact>
            {
                Kind = persistedState is null ? Inferred : Persisted,
                SourceItem = sourceItem,
                DestinationItem = destinationItem,
                PersistedState = persistedState,
                SourceActivity = sourceItem is null ? Absent : Created,
                DestinationActivity = destinationItem is null ? Absent : Created,
            },
        };

    private static CanonicalContact CreateContact(string id, string displayName, string endpointName) =>
        new()
        {
            DisplayName = displayName,
            Provenance =
            {
                ProviderId = id,
                EndpointName = endpointName,
            },
        };
}
