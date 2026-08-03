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
    public void CanonicalEvent_ToDisplayString_IncludesTitleAndDates()
    {
        var calendarEvent = new CanonicalEvent
        {
            Title = "Design Review",
            From = new DateTimeOffset(2026, 05, 14, 15, 30, 00, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 05, 14, 16, 00, 00, TimeSpan.Zero),
        };

        Assert.Equal("'Design Review' (2026-05-14 15:30 - 2026-05-14 16:00)", calendarEvent.ToDisplayString());
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
            "Create contact John Doe on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' created)",
            createAction.ToDisplayString());
        Assert.Equal(
            "Update contact John Doe on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' updated)",
            updateAction.ToDisplayString());
        Assert.Equal(
            "Delete contact John Doe on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' deleted)",
            deleteAction.ToDisplayString());
        Assert.Equal(
            "Skip contact John Doe on endpoint 'Microsoft' (Conflict: skipped per policy)",
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
            "Delete contact b1 on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' deleted)",
            action.ToDisplayString());
    }

    [Fact]
    public void SyncAction_ToDisplayString_DescribesFullAndForcedUpdates()
    {
        var sourceContact = CreateContact("a1", "John Doe", "Google");
        var targetContact = CreateContact("b1", "John Doe", "Microsoft");

        var newerAction = CreateAction(Update, SourceToDestination, sourceContact, targetContact);
        newerAction.ReasonKind = SyncActionReasonKind.Newer;

        var forcedAction = CreateAction(Update, SourceToDestination, sourceContact, targetContact);
        forcedAction.ReasonKind = SyncActionReasonKind.Forced;

        Assert.Equal(
            "Update contact John Doe on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' newer)",
            newerAction.ToDisplayString());
        Assert.Equal(
            "Update contact John Doe on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' forced despite identical content)",
            forcedAction.ToDisplayString());
    }

    [Fact]
    public void SyncAction_ToDisplayString_DescribesConflictWinnersAndBlockedWrites()
    {
        var sourceContact = CreateContact("a1", "John Doe", "Microsoft");
        var targetContact = CreateContact("b1", "John Doe", "Google");

        var winnerAction = CreateAction(Update, DestinationToSource, sourceContact, targetContact);
        winnerAction.ReasonKind = SyncActionReasonKind.ConflictWinner;
        winnerAction.WinningDirection = DestinationToSource;
        winnerAction.ConflictPolicyName = "destination-wins";

        var blockedAction = CreateAction(Skip, DestinationToSource, sourceContact, targetContact);
        blockedAction.ReasonKind = SyncActionReasonKind.ConflictNewerCannotUpdate;
        blockedAction.WinningDirection = SourceToDestination;
        blockedAction.ConflictPolicyName = "last-write-wins";

        Assert.Equal(
            "Update contact John Doe on endpoint 'Microsoft' (Reason: Contact on endpoint 'Google' wins per destination-wins policy)",
            winnerAction.ToDisplayString());
        Assert.Equal(
            "Skip contact John Doe on endpoint 'Microsoft' (Conflict: Contact on endpoint 'Microsoft' newer, cannot update per last-write-wins policy)",
            blockedAction.ToDisplayString());
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
