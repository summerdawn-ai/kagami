using System.Text;
using System.Text.Json;

using Azure.Core;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class MicrosoftEventsConnectorTests
{
    [Fact]
    public async Task GetCursorItemsAsync_UsesV1EventsDeltaEndpointWithStartDateTimeAndFiltersNotRespondedInvites()
    {
        var handler = new SequenceHttpHandler(
            CreateJsonResponse("""
                {
                  "value": [
                    {
                      "id": "accepted-1",
                      "subject": "Accepted Invite",
                      "start": { "dateTime": "2026-05-01T10:00:00", "timeZone": "UTC" },
                      "end": { "dateTime": "2026-05-01T11:00:00", "timeZone": "UTC" },
                      "responseStatus": { "response": "accepted" },
                      "isOrganizer": false
                    },
                    {
                      "id": "pending-1",
                      "subject": "Pending Invite",
                      "start": { "dateTime": "2026-05-01T12:00:00", "timeZone": "UTC" },
                      "end": { "dateTime": "2026-05-01T13:00:00", "timeZone": "UTC" },
                      "responseStatus": { "response": "notResponded" },
                      "isOrganizer": false
                    },
                    {
                      "id": "organizer-1",
                      "subject": "Organizer Event",
                      "start": { "dateTime": "2026-05-01T14:00:00", "timeZone": "UTC" },
                      "end": { "dateTime": "2026-05-01T15:00:00", "timeZone": "UTC" },
                      "isOrganizer": true
                    }
                  ],
                  "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$deltatoken=abc"
                }
                """));

        var connector = CreateConnector(handler);

        var result = await connector.GetCursorItemsAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, e => e.Provenance.ProviderId == "accepted-1");
        Assert.DoesNotContain(result.Items, e => e.Provenance.ProviderId == "pending-1");
        Assert.Contains(result.Items, e => e.Provenance.ProviderId == "organizer-1");
        Assert.Equal("https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$deltatoken=abc", result.Cursor);
        Assert.Single(handler.RequestUris);
        Assert.Contains("/v1.0/users/user%40contoso.com/events/delta", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("startDateTime=", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.DoesNotContain("calendarView", handler.RequestUris[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCursorItemsAsync_FollowsNextLinkAndReturnsFinalDeltaCursor()
    {
        var connector = CreateConnector(
            """
            {
              "value": [
                {
                  "id": "first-page",
                  "subject": "First Page",
                  "start": { "dateTime": "2026-05-01T10:00:00", "timeZone": "UTC" },
                  "end": { "dateTime": "2026-05-01T11:00:00", "timeZone": "UTC" },
                  "isOrganizer": true
                }
              ],
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$skiptoken=next"
            }
            """,
            """
            {
              "value": [
                {
                  "id": "second-page",
                  "subject": "Second Page",
                  "start": { "dateTime": "2026-05-01T12:00:00", "timeZone": "UTC" },
                  "end": { "dateTime": "2026-05-01T13:00:00", "timeZone": "UTC" },
                  "isOrganizer": true
                }
              ],
              "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$deltatoken=final"
            }
            """);

        var result = await connector.GetCursorItemsAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$deltatoken=final", result.Cursor);
    }

    [Fact]
    public async Task GetCursorItemsAsync_MapsRemovedEntryAsDeleted()
    {
        var connector = CreateConnector("""
            {
              "value": [
                {
                  "id": "deleted-1",
                  "@removed": { "reason": "deleted" }
                }
              ],
              "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$deltatoken=final"
            }
            """);

        var result = await connector.GetCursorItemsAsync(null, CancellationToken.None);

        var deleted = Assert.Single(result.Items);
        Assert.True(deleted.IsDeleted);
        Assert.Equal("deleted-1", deleted.Provenance.ProviderId);
    }

    [Fact]
    public async Task GetCursorItemsAsync_GoneWithSavedCursor_ThrowsExpiredCursorException()
    {
        var connector = CreateConnector(new SequenceHttpHandler(CreateResponse(System.Net.HttpStatusCode.Gone, """{"error":{"code":"SyncStateNotFound"}}""")));

        await Assert.ThrowsAsync<ExpiredCursorException>(() =>
            connector.GetCursorItemsAsync("https://graph.microsoft.com/v1.0/users/user@contoso.com/events/delta?$deltatoken=expired", CancellationToken.None));
    }

    [Fact]
    public void ConvertEvent_MapsCoreFields()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "event-1",
              "@odata.etag": "W/\"etag-1\"",
              "changeKey": "ck1",
              "subject": "Planning",
              "body": { "content": "Agenda" },
              "start": { "dateTime": "2026-05-01T10:00:00", "timeZone": "UTC" },
              "end": { "dateTime": "2026-05-01T11:00:00", "timeZone": "UTC" },
              "location": { "displayName": "Room 1" },
              "organizer": { "emailAddress": { "address": "organizer@contoso.com", "name": "Organizer" } },
              "attendees": [
                {
                  "emailAddress": { "address": "attendee@contoso.com", "name": "Attendee" },
                  "status": { "response": "accepted" }
                }
              ],
              "responseStatus": { "response": "accepted" },
              "isOrganizer": false,
              "recurrence": { "pattern": { "type": "daily", "interval": 1 }, "range": { "type": "noEnd", "startDate": "2026-05-01" } }
            }
            """);

        var item = MicrosoftEventsConnector.ConvertEvent(document.RootElement, "microsoft");

        Assert.NotNull(item);
        Assert.Equal("Planning", item!.Title);
        Assert.Equal("Agenda", item.Description);
        Assert.Equal("Room 1", item.Location);
        Assert.Equal("organizer@contoso.com", item.Organizer?.Email);
        Assert.Single(item.Attendees);
        Assert.NotNull(item.RecurrencePattern);
        Assert.Equal("W/\"etag-1\"", item.Provenance.Version);
        Assert.Equal("ck1", item.Metadata["microsoft.changeKey"]);
    }

    [Fact]
    public void ConvertEvent_UsesTimeZoneFieldForOffsetlessDateTimes()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "event-2",
              "subject": "Local Time Meeting",
              "start": { "dateTime": "2026-05-01T10:00:00", "timeZone": "Pacific Standard Time" },
              "end": { "dateTime": "2026-05-01T11:30:00", "timeZone": "Pacific Standard Time" },
              "isOrganizer": true
            }
            """);

        var item = MicrosoftEventsConnector.ConvertEvent(document.RootElement, "microsoft");

        Assert.NotNull(item);
        Assert.Equal(new DateTimeOffset(2026, 05, 01, 17, 00, 00, TimeSpan.Zero), item!.From);
        Assert.Equal(new DateTimeOffset(2026, 05, 01, 18, 30, 00, TimeSpan.Zero), item.To);
    }

    [Fact]
    public void BuildWritableEvent_WritesOrganizerAttendeesAndRecurrence()
    {
        var item = new CanonicalEvent
        {
            Title = "Standup",
            Description = "Daily sync",
            From = new DateTimeOffset(2026, 05, 01, 08, 00, 00, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 05, 01, 08, 15, 00, TimeSpan.Zero),
            Location = "Teams",
            RecurrencePattern = """{"pattern":{"type":"daily","interval":1},"range":{"type":"noEnd","startDate":"2026-05-01"}}""",
            Organizer = new CalendarEventParticipant { Email = "organizer@contoso.com", Name = "Organizer" },
            Attendees =
            [
                new CalendarEventParticipant { Email = "attendee@contoso.com", Name = "Attendee" }
            ]
        };

        string json = MicrosoftEventsConnector.BuildWritableEvent(item).ToJsonString();

        Assert.Contains("\"subject\":\"Standup\"", json, StringComparison.Ordinal);
        Assert.Contains("\"organizer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"attendees\"", json, StringComparison.Ordinal);
        Assert.Contains("\"recurrence\"", json, StringComparison.Ordinal);
    }

    private static MicrosoftEventsConnector CreateConnector(params string[] responseJsons)
    {
        return CreateConnector(new SequenceHttpHandler(responseJsons.Select(CreateJsonResponse).ToArray()));
    }

    private static MicrosoftEventsConnector CreateConnector(SequenceHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var credential = new MicrosoftClientCredential(new FakeTokenCredential());
        var endpoint = new EndpointOptions
        {
            Type = EndpointOptions.MicrosoftCalendar,
            Properties = new Dictionary<string, string>
            {
                ["userId"] = "user@contoso.com",
            }
        };

        return new MicrosoftEventsConnector(httpClient, "test", endpoint, credential);
    }

    private static HttpResponseMessage CreateJsonResponse(string json) =>
        CreateResponse(System.Net.HttpStatusCode.OK, json);

    private static HttpResponseMessage CreateResponse(System.Net.HttpStatusCode statusCode, string json) =>
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
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
