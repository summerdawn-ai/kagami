using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class ContentHashHelperTests
{
    [Fact]
    public void ComputeContentHash_ReturnsStableHashForCanonicalEvent()
    {
        var first = CreateEvent("google-1", "Google");
        var second = CreateEvent("microsoft-1", "Microsoft");

        string firstHash = ContentHashHelper.ComputeContentHash(first);
        string secondHash = ContentHashHelper.ComputeContentHash(second);

        Assert.NotEmpty(firstHash);
        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public void ComputeContentHash_IgnoresEventParticipants()
    {
        var withoutParticipants = CreateEvent("google-1", "Google");
        var withParticipants = CreateEvent("microsoft-1", "Microsoft");
        withParticipants.Organizer = new CalendarEventParticipant { Email = "organizer@example.test" };
        withParticipants.Attendees.Add(new CalendarEventParticipant { Email = "attendee@example.test" });

        Assert.Equal(
            ContentHashHelper.ComputeContentHash(withoutParticipants),
            ContentHashHelper.ComputeContentHash(withParticipants));
        Assert.True(ContentHashHelper.HaveIdenticalContent(withoutParticipants, withParticipants));
    }

    [Fact]
    public void ComputeContentHash_IgnoresEventICalUid()
    {
        var first = CreateEvent("google-1", "Google");
        var second = CreateEvent("microsoft-1", "Microsoft") with { ICalUid = "provider-generated@example.test" };

        Assert.Equal(
            ContentHashHelper.ComputeContentHash(first),
            ContentHashHelper.ComputeContentHash(second));
        Assert.True(ContentHashHelper.HaveIdenticalContent(first, second));
    }

    [Fact]
    public void ComputeContentHash_IgnoresDescriptionBeyondSharedProviderLimit()
    {
        var truncated = CreateEvent("google-1", "Google") with { Description = new string('a', 8_192) };
        var complete = CreateEvent("microsoft-1", "Microsoft") with { Description = new string('a', 8_193) };

        Assert.Equal(
            ContentHashHelper.ComputeContentHash(truncated),
            ContentHashHelper.ComputeContentHash(complete));
        Assert.True(ContentHashHelper.HaveIdenticalContent(truncated, complete));
    }

    [Fact]
    public void ComputeContentHash_DiffersForAllDayRepresentation()
    {
        var timed = CreateEvent("google-1", "Google");
        var allDay = timed with { IsAllDay = true };

        Assert.NotEqual(
            ContentHashHelper.ComputeContentHash(timed),
            ContentHashHelper.ComputeContentHash(allDay));
    }

    private static CanonicalEvent CreateEvent(string providerId, string endpointName) => new()
    {
        Title = "Design Review",
        Description = "Agenda",
        From = new DateTimeOffset(2026, 05, 14, 15, 30, 00, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 05, 14, 16, 00, 00, TimeSpan.Zero),
        Location = "Zoom",
        ICalUid = "design-review@example.test",
        Provenance =
        {
            ProviderId = providerId,
            EndpointName = endpointName,
            Version = "v1",
        }
    };
}
