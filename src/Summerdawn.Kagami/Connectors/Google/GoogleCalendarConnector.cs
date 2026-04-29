
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors.Google;

internal sealed class GoogleCalendarConnector : IConnector
{
    private readonly HttpClient httpClient;
    private readonly GoogleOAuthCredential credential;
    private readonly string calendarId;

    public GoogleCalendarConnector(
        HttpClient httpClient,
        string endpointName,
        EndpointOptions endpoint,
        GoogleOAuthCredential credential,
        ILogger<GoogleCalendarConnector> logger)
    {
        _ = logger;
        this.httpClient = httpClient;
        this.credential = credential;
        calendarId = endpoint.Properties.GetOptionalValue("calendarId") ?? "primary";
        _ = endpointName;
    }

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.GoogleCalendar,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = false,
    };

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetAccessTokenAsync(cancellationToken);
    }

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        GetEventsPageAsync(null, null, cancellationToken);

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default)
    {
        var (syncToken, pageToken) = ParseCursor(cursor);
        return GetEventsPageAsync(syncToken, pageToken, cancellationToken);
    }

    public async Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(id)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        return ConvertEvent(document.RootElement);
    }

    public async Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events";
        using var request = await CreateRequestAsync(HttpMethod.Post, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(GetEventPayload(item)));
        using var document = await SendForJsonAsync(request, cancellationToken);
        var created = ConvertEvent(document.RootElement)
            ?? throw new InvalidOperationException("Google Calendar create returned no event payload.");
        return await GetItemAsync(created.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Google Calendar create succeeded but the created item could not be reloaded.");
    }

    public async Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(item.SourceId)}";
        using var request = await CreateRequestAsync(HttpMethod.Put, requestUri, cancellationToken);
        var body = BuildWritableEvent(GetEventPayload(item));
        body["id"] = item.SourceId;
        request.Content = CreateJsonContent(body);
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            request.Headers.TryAddWithoutValidation("If-Match", item.Version);
        }

        using var document = await SendForJsonAsync(request, cancellationToken);
        return ConvertEvent(document.RootElement)
            ?? throw new InvalidOperationException("Google Calendar update returned no event payload.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(id)}";
        using var request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<IncrementalPage> GetEventsPageAsync(string? syncToken, string? pageToken, CancellationToken cancellationToken)
    {
        StringBuilder requestUri = new($"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events");
        requestUri.Append("?maxResults=250&showDeleted=true&singleEvents=true");

        if (!string.IsNullOrWhiteSpace(syncToken))
        {
            requestUri.Append("&syncToken=").Append(Uri.EscapeDataString(syncToken));
        }

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            requestUri.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        }

        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri.ToString(), cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        // HTTP 410 Gone: sync token expired; fall back to full sync
        if (response.StatusCode == HttpStatusCode.Gone && syncToken is not null)
        {
            return await GetEventsPageAsync(null, null, cancellationToken);
        }

        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        List<CanonicalItem> items = [];
        if (document.RootElement.TryGetProperty("items", out var events))
        {
            foreach (var eventElement in events.EnumerateArray())
            {
                var item = ConvertEvent(eventElement);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        string? nextPageToken = document.RootElement.TryGetProperty("nextPageToken", out var nextPageTokenElement)
            ? nextPageTokenElement.GetString()
            : null;
        string? nextSyncToken = document.RootElement.TryGetProperty("nextSyncToken", out var nextSyncTokenElement)
            ? nextSyncTokenElement.GetString()
            : null;

        string? cursor = nextPageToken is not null
            ? SerializeCursor(syncToken, nextPageToken)
            : nextSyncToken is not null
                ? SerializeCursor(nextSyncToken, null)
                : null;

        return new IncrementalPage
        {
            Items = items,
            HasMore = nextPageToken is not null,
            NextCursor = cursor,
        };
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string requestUri, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(method, requestUri);
        string accessToken = await credential.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Google Calendar API request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static StringContent CreateJsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject BuildWritableEvent(CanonicalCalendarEvent calendarEvent)
    {
        JsonObject body = new()
        {
            ["summary"] = calendarEvent.Subject,
        };

        if (!string.IsNullOrWhiteSpace(calendarEvent.Body))
        {
            body["description"] = calendarEvent.Body;
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
        {
            body["location"] = calendarEvent.Location;
        }

        if (calendarEvent.IsAllDay)
        {
            body["start"] = new JsonObject
            {
                ["date"] = calendarEvent.Start.UtcDateTime.ToString("yyyy-MM-dd"),
            };
            body["end"] = new JsonObject
            {
                ["date"] = calendarEvent.End.UtcDateTime.ToString("yyyy-MM-dd"),
            };
        }
        else
        {
            body["start"] = new JsonObject
            {
                ["dateTime"] = calendarEvent.Start.UtcDateTime.ToString("o"),
                ["timeZone"] = "UTC",
            };
            body["end"] = new JsonObject
            {
                ["dateTime"] = calendarEvent.End.UtcDateTime.ToString("o"),
                ["timeZone"] = "UTC",
            };
        }

        if (calendarEvent.AttendeeEmails.Count > 0)
        {
            var attendeesArray = new JsonArray();
            foreach (string email in calendarEvent.AttendeeEmails)
            {
                attendeesArray.Add((JsonNode?)new JsonObject { ["email"] = email });
            }

            body["attendees"] = attendeesArray;
        }

        return body;
    }

    private static CanonicalCalendarEvent GetEventPayload(CanonicalItem item) =>
        item.Payload as CanonicalCalendarEvent
        ?? throw new InvalidOperationException("Google Calendar connector only supports CanonicalCalendarEvent payloads.");

    private static CanonicalItem? ConvertEvent(JsonElement element)
    {
        string? id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Deleted events have status "cancelled"
        string? status = element.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return new CanonicalItem
            {
                EntityType = EntityType.CalendarEvent,
                SourceId = id,
                IsDeleted = true,
            };
        }

        bool isAllDay = element.TryGetProperty("start", out var startElement)
            && startElement.TryGetProperty("date", out _)
            && !startElement.TryGetProperty("dateTime", out _);

        CanonicalCalendarEvent calendarEvent = new()
        {
            Subject = ReadString(element, "summary") ?? string.Empty,
            Body = ReadString(element, "description"),
            Location = ReadString(element, "location"),
            IsAllDay = isAllDay,
            OrganizerEmail = ReadOrganizerEmail(element),
            ICalUid = ReadString(element, "iCalUID"),
            IsRecurring = element.TryGetProperty("recurringEventId", out _),
            LastModified = ReadDateTimeOffset(element, "updated"),
        };

        calendarEvent.Start = ReadEventDateTime(element, "start", isAllDay);
        calendarEvent.End = ReadEventDateTime(element, "end", isAllDay);

        if (element.TryGetProperty("attendees", out var attendees))
        {
            foreach (var attendee in attendees.EnumerateArray())
            {
                string? email = ReadString(attendee, "email");
                if (!string.IsNullOrWhiteSpace(email))
                {
                    calendarEvent.AttendeeEmails.Add(email);
                }
            }
        }

        return CanonicalItemSerializer.WithComputedHash(new CanonicalItem
        {
            EntityType = EntityType.CalendarEvent,
            Payload = calendarEvent,
            SourceId = id,
            Version = ReadString(element, "etag"),
            Metadata = [],
        });
    }

    private static DateTimeOffset ReadEventDateTime(JsonElement element, string propertyName, bool isAllDay)
    {
        if (!element.TryGetProperty(propertyName, out var dtElement))
        {
            return default;
        }

        if (isAllDay)
        {
            string? dateValue = ReadString(dtElement, "date");
            if (!string.IsNullOrWhiteSpace(dateValue) && DateOnly.TryParse(dateValue, out var dateOnly))
            {
                return new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            }
        }
        else
        {
            string? dateTimeValue = ReadString(dtElement, "dateTime");
            if (!string.IsNullOrWhiteSpace(dateTimeValue) && DateTimeOffset.TryParse(dateTimeValue, out var dto))
            {
                return dto;
            }
        }

        return default;
    }

    private static string? ReadOrganizerEmail(JsonElement element)
    {
        if (!element.TryGetProperty("organizer", out var organizer))
        {
            return null;
        }

        return ReadString(organizer, "email");
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var dto) ? dto : null;
    }

    private static string SerializeCursor(string? syncToken, string? pageToken)
    {
        var parts = new JsonObject();
        if (!string.IsNullOrWhiteSpace(syncToken))
        {
            parts["s"] = syncToken;
        }

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            parts["p"] = pageToken;
        }

        return parts.ToJsonString();
    }

    private static (string? syncToken, string? pageToken) ParseCursor(string cursor)
    {
        try
        {
            using var doc = JsonDocument.Parse(cursor);
            var root = doc.RootElement;
            string? syncToken = root.TryGetProperty("s", out var sElem) ? sElem.GetString() : null;
            string? pageToken = root.TryGetProperty("p", out var pElem) ? pElem.GetString() : null;
            return (syncToken, pageToken);
        }
        catch (JsonException)
        {
            // Legacy: treat the entire cursor as a sync token
            return (cursor, null);
        }
    }
}
