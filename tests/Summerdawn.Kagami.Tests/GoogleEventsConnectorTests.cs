using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class GoogleEventsConnectorTests
{
    [Fact]
    public async Task GetCursorItemsAsync_FiltersOutNeedsActionInvites()
    {
        var handler = new SequenceHttpHandler(CreateJsonResponse(HttpStatusCode.OK, """
            {
              "items": [
                {
                  "id": "accepted-1",
                  "etag": "\"etag1\"",
                  "summary": "Accepted",
                  "status": "confirmed",
                  "start": { "dateTime": "2026-05-01T10:00:00Z" },
                  "end": { "dateTime": "2026-05-01T11:00:00Z" },
                  "attendees": [
                    { "email": "user@contoso.com", "self": true, "responseStatus": "accepted" }
                  ]
                },
                {
                  "id": "pending-1",
                  "etag": "\"etag2\"",
                  "summary": "Pending",
                  "status": "confirmed",
                  "start": { "dateTime": "2026-05-01T12:00:00Z" },
                  "end": { "dateTime": "2026-05-01T13:00:00Z" },
                  "attendees": [
                    { "email": "user@contoso.com", "self": true, "responseStatus": "needsAction" }
                  ]
                },
                {
                  "id": "organizer-1",
                  "etag": "\"etag3\"",
                  "summary": "Organizer Event",
                  "status": "confirmed",
                  "start": { "dateTime": "2026-05-01T14:00:00Z" },
                  "end": { "dateTime": "2026-05-01T15:00:00Z" },
                  "organizer": { "email": "user@contoso.com", "self": true }
                }
              ],
              "nextSyncToken": "sync-token"
            }
            """));
        var connector = CreateConnector(handler);

        var result = await connector.GetCursorItemsAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Provenance.ProviderId == "accepted-1");
        Assert.DoesNotContain(result.Items, e => e.Provenance.ProviderId == "pending-1");
        Assert.Contains(result.Items, e => e.Provenance.ProviderId == "organizer-1");
        Assert.NotNull(result.Cursor);
        Assert.Single(handler.RequestUris);
        Assert.Contains("eventTypes=default", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("timeMin=", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCursorItemsAsync_WithSavedCursor_DoesNotApplyTimeMin()
    {
        var handler = new SequenceHttpHandler(CreateJsonResponse(HttpStatusCode.OK, """
            {
              "items": [],
              "nextSyncToken": "next-sync-token"
            }
            """));
        var connector = CreateConnector(handler);

        _ = await connector.GetCursorItemsAsync("""{"syncToken":"saved-sync-token","pageToken":null}""", CancellationToken.None);

        Assert.Single(handler.RequestUris);
        Assert.DoesNotContain("timeMin=", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("syncToken=saved-sync-token", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertEvent_ReturnsNullForBirthdayEvents()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "birthday-1",
              "status": "confirmed",
              "eventType": "birthday",
              "summary": "Birthday",
              "start": { "date": "2026-05-01" },
              "end": { "date": "2026-05-02" }
            }
            """);

        var item = GoogleEventsConnector.ConvertEvent(document.RootElement, "google");

        Assert.Null(item);
    }

    [Fact]
    public void ConvertEvent_MapsCoreFields()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "event-1",
              "etag": "\"etag\"",
              "summary": "Planning",
              "description": "Agenda",
              "location": "Room 1",
              "iCalUID": "ical-123",
              "recurrence": [ "RRULE:FREQ=WEEKLY;BYDAY=MO" ],
              "start": { "dateTime": "2026-05-01T10:00:00Z" },
              "end": { "dateTime": "2026-05-01T11:00:00Z" },
              "organizer": { "email": "organizer@contoso.com", "displayName": "Organizer" },
              "attendees": [
                { "email": "attendee@contoso.com", "displayName": "Attendee", "responseStatus": "accepted" }
              ]
            }
            """);

        var item = GoogleEventsConnector.ConvertEvent(document.RootElement, "google");

        Assert.NotNull(item);
        Assert.Equal("Planning", item!.Title);
        Assert.Equal("Agenda", item.Description);
        Assert.Equal("Room 1", item.Location);
        Assert.Equal("ical-123", item.ICalUid);
        Assert.Equal("RRULE:FREQ=WEEKLY;BYDAY=MO", item.RecurrencePattern);
        Assert.Equal("organizer@contoso.com", item.Organizer?.Email);
        Assert.Single(item.Attendees);
    }

    [Fact]
    public void ConvertEvent_UsesHangoutLinkAsVirtualLocationWhenLocationIsMissing()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "event-2",
              "etag": "\"etag\"",
              "summary": "Remote meeting",
              "status": "confirmed",
              "start": { "dateTime": "2026-05-01T10:00:00+02:00", "timeZone": "Europe/Berlin" },
              "end": { "dateTime": "2026-05-01T11:00:00+02:00", "timeZone": "Europe/Berlin" },
              "organizer": { "email": "organizer@contoso.com" },
              "hangoutLink": "https://meet.google.com/mrw-mbth-xqo"
            }
            """);

        var item = GoogleEventsConnector.ConvertEvent(document.RootElement, "google");

        Assert.NotNull(item);
        Assert.Equal("https://meet.google.com/mrw-mbth-xqo", item!.Location);
    }

    [Fact]
    public void ConvertEvent_MapsAllDayDateValuesAndComputesHash()
    {
        using var document = JsonDocument.Parse("""
          {
          "id": "all-day",
          "summary": "Blocker",
          "start": { "date": "2026-08-01" },
          "end": { "date": "2026-08-02" }
          }
          """);

        var item = GoogleEventsConnector.ConvertEvent(document.RootElement, "GoogleEvents");

        Assert.NotNull(item);
        Assert.True(item!.IsAllDay);
        Assert.Equal(new DateTimeOffset(2026, 08, 01, 00, 00, 00, TimeSpan.Zero), item.From);
        Assert.NotNull(item.Provenance.ContentHash);
    }

    [Fact]
    public void BuildWritableEvent_OmitsOrganizerAndAttendees()
    {
        var item = new CanonicalEvent
        {
            Title = "Standup",
            Description = "Daily sync",
            From = new DateTimeOffset(2026, 05, 01, 08, 00, 00, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 05, 01, 08, 15, 00, TimeSpan.Zero),
            Location = "Teams",
            RecurrencePattern = "RRULE:FREQ=DAILY",
            Organizer = new CalendarEventParticipant { Email = "organizer@contoso.com", Name = "Organizer" },
            Attendees =
            [
                new CalendarEventParticipant { Email = "attendee@contoso.com", Name = "Attendee", ResponseStatus = "accepted" }
            ]
        };

        string json = GoogleEventsConnector.BuildWritableEvent(item).ToJsonString();

        Assert.Contains("\"summary\":\"Standup\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"organizer\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"attendees\"", json, StringComparison.Ordinal);
        Assert.Contains("\"recurrence\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWritableEvent_WritesAllDayDateValues()
    {
        var item = new CanonicalEvent
        {
            Title = "Blocker",
            IsAllDay = true,
            From = new DateTimeOffset(2026, 08, 01, 00, 00, 00, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 08, 02, 00, 00, 00, TimeSpan.Zero),
        };

        string json = GoogleEventsConnector.BuildWritableEvent(item).ToJsonString();

        Assert.Contains("\"start\":{\"date\":\"2026-08-01\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"end\":{\"date\":\"2026-08-02\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("dateTime", json, StringComparison.Ordinal);
    }

    private static GoogleEventsConnector CreateConnector(SequenceHttpHandler connectorHandler)
    {
        string endpointName = $"google-calendar-test-{Guid.NewGuid():N}";
        var tokenCache = new GoogleTokenCache(Path.Combine(Path.GetTempPath(), "kagami-tests", Guid.NewGuid().ToString("N")));
        tokenCache.Save(endpointName, new GoogleTokenCacheEntry("fake-token", "fake-refresh-token", DateTimeOffset.UtcNow.AddHours(1), "user@contoso.com"));

        var credential = new GoogleOAuthCredential(
            "client-id",
            "client-secret",
            endpointName,
            "user@contoso.com",
            ["https://www.googleapis.com/auth/calendar.readonly"],
        new HttpClient(new SequenceHttpHandler()),
        tokenCache);

        var endpoint = new EndpointOptions
        {
            Type = EndpointOptions.GoogleEvents,
            Properties = [],
        };

        return new GoogleEventsConnector(
            new HttpClient(connectorHandler),
            "google",
            endpoint,
            credential,
            NullLogger<GoogleEventsConnector>.Instance);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class SequenceHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queuedResponses = new(responses);
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            if (!queuedResponses.TryDequeue(out var response))
            {
                throw new InvalidOperationException($"Unexpected HTTP request to {request.RequestUri}");
            }

            return Task.FromResult(response);
        }
    }
}
