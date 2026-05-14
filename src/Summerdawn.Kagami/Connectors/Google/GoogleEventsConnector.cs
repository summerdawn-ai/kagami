using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Synchronizes Google events with silent write operations.
/// </summary>
public class GoogleEventsConnector(HttpClient httpClient, string endpointName, EndpointOptions endpoint, GoogleOAuthCredential credential) : IConnector<CanonicalEvent>
{
    private const string GraphBaseUri = "https://www.googleapis.com/calendar/v3";
    private const int PageSize = 250;
    private readonly string collectionPath = GetCollectionPath(endpointName, endpoint);

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

    public string EndpointName { get; } = endpointName;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetAccessTokenAsync(cancellationToken);
    }

    public async Task<ItemSet<CanonicalEvent>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        var parsedCursor = string.IsNullOrWhiteSpace(cursor)
            ? new GoogleCursor(null, null)
            : ParseCursor(cursor);

        List<CanonicalEvent> items = [];
        string? finalCursor = cursor;
        while (true)
        {
            var page = await GetEventsPageAsync(parsedCursor, cancellationToken);
            items.AddRange(page.Items);
            finalCursor = page.Cursor;
            if (!page.HasMore)
            {
                break;
            }

            if (page.Cursor is null)
            {
                throw new InvalidOperationException("Google calendar connector returned HasMore=true without a cursor.");
            }

            parsedCursor = ParseCursor(page.Cursor);
        }

        return new ItemSet<CanonicalEvent>(items, finalCursor);
    }

    public async Task<CanonicalEvent?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var item = ConvertEvent(document.RootElement, EndpointName);
        return item is not null && ShouldSync(item) ? item : null;
    }

    public async Task<CanonicalEvent> CreateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/import";
        using var request = await CreateRequestAsync(HttpMethod.Post, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(item));
        using var document = await SendForJsonAsync(request, cancellationToken);
        string id = ReadString(document.RootElement, "id")
            ?? throw new InvalidOperationException("Google calendar import returned no event id.");
        return await GetItemAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Google calendar import succeeded but the created event could not be reloaded.");
    }

    public async Task<CanonicalEvent> UpdateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(item.Provenance.ProviderId)}?sendUpdates=none";
        using var request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(item));
        using var _ = await SendForJsonAsync(request, cancellationToken);

        return await GetItemAsync(item.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Google calendar update succeeded but the updated event could not be reloaded.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}?sendUpdates=none";
        using var request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<ConnectorPage> GetEventsPageAsync(GoogleCursor cursor, CancellationToken cancellationToken)
    {
        // Full load: exclude deleted events and non-default event types up front.
        // Incremental sync: showDeleted is not allowed (Google always returns cancellations as changed items).
        bool isIncremental = !string.IsNullOrWhiteSpace(cursor.SyncToken);
        StringBuilder requestUri = new($"{collectionPath}?singleEvents=false&eventTypes=default&maxResults={PageSize}");
        if (!isIncremental)
        {
            requestUri.Append("&showDeleted=false");
            requestUri.Append("&timeMin=").Append(Uri.EscapeDataString(GetFullLoadTimeMin(DateTimeOffset.UtcNow)));
        }

        if (!string.IsNullOrWhiteSpace(cursor.PageToken))
        {
            requestUri.Append("&pageToken=").Append(Uri.EscapeDataString(cursor.PageToken));
        }

        if (!string.IsNullOrWhiteSpace(cursor.SyncToken))
        {
            requestUri.Append("&syncToken=").Append(Uri.EscapeDataString(cursor.SyncToken));
        }

        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri.ToString(), cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone && !string.IsNullOrWhiteSpace(cursor.SyncToken))
        {
            throw new ExpiredCursorException("Google Calendar sync token expired.");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        List<CanonicalEvent> items = [];
        if (document.RootElement.TryGetProperty("items", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in values.EnumerateArray())
            {
                var item = ConvertEvent(element, EndpointName);
                if (item is null)
                {
                    continue;
                }

                if (item.IsDeleted || ShouldSync(item))
                {
                    items.Add(item);
                }
            }
        }

        string? nextPageToken = ReadString(document.RootElement, "nextPageToken");
        string? nextSyncToken = ReadString(document.RootElement, "nextSyncToken") ?? cursor.SyncToken;
        string? nextCursor = nextPageToken is not null
            ? SerializeCursor(new GoogleCursor(cursor.SyncToken, nextPageToken))
            : nextSyncToken is not null ? SerializeCursor(new GoogleCursor(nextSyncToken, null)) : null;

        return new ConnectorPage(items, nextCursor, nextPageToken is not null);
    }

    private static bool ShouldSync(CanonicalEvent item)
    {
        if (item.Metadata.TryGetValue("google.isOrganizer", out string? isOrganizerRaw)
            && bool.TryParse(isOrganizerRaw, out bool isOrganizer)
            && isOrganizer)
        {
            return true;
        }

        string? selfResponse = item.Metadata.GetValueOrDefault("google.selfResponse");
        return selfResponse is "accepted" or "tentative";
    }

    internal static CanonicalEvent? ConvertEvent(JsonElement element, string endpointName)
    {
        string? id = ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string? status = ReadString(element, "status");
        if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return new CanonicalEvent
            {
                Provenance =
                {
                    ProviderId = id,
                    EndpointName = endpointName,
                    Version = ReadString(element, "etag"),
                    LastModified = ReadDateTimeOffset(element, "updated"),
                },
                IsDeleted = true,
            };
        }

        string? eventType = ReadString(element, "eventType");
        if (!string.IsNullOrWhiteSpace(eventType)
            && !string.Equals(eventType, "default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var item = new CanonicalEvent
        {
            Title = ReadString(element, "summary") ?? string.Empty,
            Description = ReadString(element, "description"),
            From = ReadEventDateTime(element, "start"),
            To = ReadEventDateTime(element, "end"),
            Location = ReadLocation(element),
            RecurrencePattern = ReadRecurrencePattern(element),
            Organizer = ReadParticipant(element, "organizer"),
            Attendees = ReadAttendees(element),
            ICalUid = ReadString(element, "iCalUID"),
            Provenance =
            {
                ProviderId = id,
                EndpointName = endpointName,
                Version = ReadString(element, "etag"),
                LastModified = ReadDateTimeOffset(element, "updated"),
            },
        };

        bool isOrganizer = element.TryGetProperty("organizer", out var organizerElement)
            && organizerElement.TryGetProperty("self", out var organizerSelf)
            && organizerSelf.ValueKind == JsonValueKind.True;
        if (!isOrganizer
            && element.TryGetProperty("creator", out var creatorElement)
            && creatorElement.TryGetProperty("self", out var creatorSelf)
            && creatorSelf.ValueKind == JsonValueKind.True)
        {
            isOrganizer = true;
        }

        item.Metadata["google.isOrganizer"] = isOrganizer.ToString();
        if (element.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var attendee in attendees.EnumerateArray())
            {
                bool isSelf = attendee.TryGetProperty("self", out var selfElement) && selfElement.ValueKind == JsonValueKind.True;
                if (!isSelf)
                {
                    continue;
                }

                string? selfResponse = ReadString(attendee, "responseStatus");
                if (!string.IsNullOrWhiteSpace(selfResponse))
                {
                    item.Metadata["google.selfResponse"] = selfResponse;
                }

                break;
            }
        }

        return item;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Calendar writable payload uses known JsonNode shapes.")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "Calendar writable payload uses known JsonNode shapes.")]
    internal static JsonObject BuildWritableEvent(CanonicalEvent item)
    {
        JsonObject body = new()
        {
            ["summary"] = item.Title,
            ["description"] = item.Description,
            ["location"] = item.Location,
            ["start"] = CreateEventDateTimeNode(item.From),
            ["end"] = CreateEventDateTimeNode(item.To),
        };

        if (!string.IsNullOrWhiteSpace(item.ICalUid))
        {
            body["iCalUID"] = item.ICalUid;
        }

        if (!string.IsNullOrWhiteSpace(item.RecurrencePattern))
        {
            body["recurrence"] = new JsonArray(item.RecurrencePattern
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(rule => (JsonNode?)rule)
                .ToArray());
        }

        if (item.Organizer is not null && !string.IsNullOrWhiteSpace(item.Organizer.Email))
        {
            body["organizer"] = new JsonObject
            {
                ["email"] = item.Organizer.Email,
                ["displayName"] = item.Organizer.Name,
            };
        }

        if (item.Attendees.Count > 0)
        {
            JsonArray attendees = [];
            foreach (var attendee in item.Attendees)
            {
                if (string.IsNullOrWhiteSpace(attendee.Email))
                {
                    continue;
                }

                attendees.Add(new JsonObject
                {
                    ["email"] = attendee.Email,
                    ["displayName"] = attendee.Name,
                    ["responseStatus"] = attendee.ResponseStatus,
                });
            }

            if (attendees.Count > 0)
            {
                body["attendees"] = attendees;
            }
        }

        return body;
    }

    private static string? ReadRecurrencePattern(JsonElement element)
    {
        if (!element.TryGetProperty("recurrence", out var recurrence) || recurrence.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string[] rules = recurrence.EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        return rules.Length == 0 ? null : string.Join('\n', rules);
    }

    private static string? ReadLocation(JsonElement element)
    {
        string? location = ReadString(element, "location");
        if (!string.IsNullOrWhiteSpace(location))
        {
            return location;
        }

        string? hangoutLink = ReadString(element, "hangoutLink");
        if (!string.IsNullOrWhiteSpace(hangoutLink))
        {
            return hangoutLink;
        }

        if (!element.TryGetProperty("conferenceData", out var conferenceData)
            || conferenceData.ValueKind != JsonValueKind.Object
            || !conferenceData.TryGetProperty("entryPoints", out var entryPoints)
            || entryPoints.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entryPoint in entryPoints.EnumerateArray())
        {
            if (!string.Equals(ReadString(entryPoint, "entryPointType"), "video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? uri = ReadString(entryPoint, "uri");
            if (!string.IsNullOrWhiteSpace(uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static List<CalendarEventParticipant> ReadAttendees(JsonElement element)
    {
        if (!element.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CalendarEventParticipant> values = [];
        foreach (var attendee in attendees.EnumerateArray())
        {
            values.Add(new CalendarEventParticipant
            {
                Name = ReadString(attendee, "displayName"),
                Email = ReadString(attendee, "email"),
                ResponseStatus = ReadString(attendee, "responseStatus"),
            });
        }

        return values;
    }

    private static CalendarEventParticipant? ReadParticipant(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var participant) || participant.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CalendarEventParticipant
        {
            Name = ReadString(participant, "displayName"),
            Email = ReadString(participant, "email"),
            ResponseStatus = ReadString(participant, "responseStatus"),
        };
    }

    private static DateTimeOffset ReadEventDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var dateNode))
        {
            return DateTimeOffset.MinValue;
        }

        string? dateTime = ReadString(dateNode, "dateTime");
        if (DateTimeOffset.TryParse(dateTime, out var parsedDateTime))
        {
            return parsedDateTime.ToUniversalTime();
        }

        string? date = ReadString(dateNode, "date");
        if (DateOnly.TryParse(date, out var parsedDate))
        {
            return new DateTimeOffset(parsedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        return DateTimeOffset.MinValue;
    }

    private static JsonObject CreateEventDateTimeNode(DateTimeOffset value) =>
        new()
        {
            ["dateTime"] = value.ToUniversalTime().ToString("o"),
            ["timeZone"] = "UTC",
        };

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        string token = await credential.GetAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
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

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Google Calendar API request failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
    }

    private static StringContent CreateJsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static string GetFullLoadTimeMin(DateTimeOffset referenceTime)
    {
        var oneYearAgo = referenceTime.ToUniversalTime().AddMonths(-12);
        var firstBeginningOfMonthAfterOneYearAgo = new DateTimeOffset(oneYearAgo.Year, oneYearAgo.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return firstBeginningOfMonthAfterOneYearAgo.AddMonths(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string GetCollectionPath(string endpointName, EndpointOptions endpoint)
    {
        string calendarId = endpoint.Properties.GetOptionalValue("calendarId") ?? "primary";
        return $"{GraphBaseUri}/calendars/{Uri.EscapeDataString(calendarId)}/events";
    }

    private static GoogleCursor ParseCursor(string cursor)
    {
        using var document = JsonDocument.Parse(cursor);
        var root = document.RootElement;
        return new GoogleCursor(
            root.TryGetProperty("syncToken", out var syncTokenElement) ? syncTokenElement.GetString() : null,
            root.TryGetProperty("pageToken", out var pageTokenElement) ? pageTokenElement.GetString() : null);
    }

    private static string SerializeCursor(GoogleCursor cursor) =>
        new JsonObject
        {
            ["syncToken"] = cursor.SyncToken,
            ["pageToken"] = cursor.PageToken,
        }.ToJsonString();

    private sealed record GoogleCursor(string? SyncToken, string? PageToken);

    private sealed record ConnectorPage(IReadOnlyList<CanonicalEvent> Items, string? Cursor, bool HasMore);
}
