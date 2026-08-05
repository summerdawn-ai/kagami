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
/// Synchronizes canonical events with a Google Calendar endpoint.
/// </summary>
/// <remarks>
/// Full reads begin one year before the current month and use Google Calendar's ordinary
/// collection pagination. Persisted reads use the Calendar API sync token and preserve
/// cancellation records so the sync engine can remove deleted events. Writes use regular event
/// creation, patch, and delete operations without sending attendee notifications. Regular
/// creation avoids Google's import operation, which treats iCalendar UIDs as upsert keys.
/// </remarks>
public class GoogleEventsConnector(HttpClient httpClient, string endpointName, EndpointOptions endpoint, GoogleOAuthCredential credential, ILogger<GoogleEventsConnector> logger) : IConnector<CanonicalEvent>
{
    private const string GraphBaseUri = "https://www.googleapis.com/calendar/v3";
    private const int PageSize = 250;
    private readonly string collectionPath = GetCollectionPath(endpointName, endpoint);

    /// <inheritdoc/>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.GoogleEvents,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = false,
    };

    /// <inheritdoc/>
    public string EndpointName { get; } = endpointName;

    /// <inheritdoc/>
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Authenticating to {Endpoint}...", EndpointName);
        _ = await credential.GetAccessTokenAsync(cancellationToken);
        logger.LogInformation("Authenticated to {Endpoint}.", EndpointName);
    }

    /// <inheritdoc/>
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

        // A provider can repeat an item across pages; retain its final observation, including a later deletion.
        items = items.GroupBy(static item => item.Provenance.ProviderId, StringComparer.Ordinal).Select(static group => group.Last()).ToList();

        return new ItemSet<CanonicalEvent>(items, finalCursor);
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var item = ConvertEvent(document.RootElement, EndpointName);
        return item is not null && ShouldSync(item) ? item : null;
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent> CreateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}?sendUpdates=none";
        using var request = await CreateRequestAsync(HttpMethod.Post, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(item));
        using var document = await SendForJsonAsync(request, cancellationToken);
        string id = ReadString(document.RootElement, "id")
            ?? throw new InvalidOperationException("Google calendar create returned no event id.");
        return await GetItemAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Google calendar create succeeded but the created event could not be reloaded.");
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent> UpdateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(item.Provenance.ProviderId)}?sendUpdates=none";
        using var request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(item));
        using var _ = await SendForJsonAsync(request, cancellationToken);

        return await GetItemAsync(item.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Google calendar update succeeded but the updated event could not be reloaded.");
    }

    /// <inheritdoc/>
    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}?sendUpdates=none";
        using var request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Reads one Google Calendar page and converts its provider cursor into the connector cursor format.
    /// </summary>
    /// <remarks>
    /// A full read suppresses cancelled events and starts at the configured rolling time boundary.
    /// An incremental read must retain Google's cancellation entries, because those entries are the
    /// only signal the sync engine receives when an event disappears from the remote calendar.
    /// </remarks>
    private async Task<ConnectorPage> GetEventsPageAsync(GoogleCursor cursor, CancellationToken cancellationToken)
    {
        // Full load: exclude deleted events and non-default event types up front.
        // Incremental sync: showDeleted is not allowed (Google always returns cancellations as changed items).
        // Google applies different query rules to initial and incremental reads. In particular,
        // showDeleted is valid for the initial collection read but incremental reads return
        // cancellation tombstones as part of the sync-token protocol.
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

        // A page token continues the current traversal; only the final page can advance the
        // durable sync token. Keeping those states distinct prevents persisting a partial cursor.
        string? nextPageToken = ReadString(document.RootElement, "nextPageToken");
        string? nextSyncToken = ReadString(document.RootElement, "nextSyncToken") ?? cursor.SyncToken;
        string? nextCursor = nextPageToken is not null
            ? SerializeCursor(new GoogleCursor(cursor.SyncToken, nextPageToken))
            : nextSyncToken is not null ? SerializeCursor(new GoogleCursor(nextSyncToken, null)) : null;

        return new ConnectorPage(items, nextCursor, nextPageToken is not null);
    }

    /// <summary>
    /// Determines whether an event is relevant to this synchronization account.
    /// </summary>
    /// <remarks>
    /// Google exposes both organizer and attendee response state. Organizers are always writable
    /// from this connector, while attendees are included only after accepting or tentatively
    /// accepting the invitation.
    /// </remarks>
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

    /// <summary>
    /// Converts a Google Calendar event payload into the canonical event representation.
    /// </summary>
    /// <remarks>
    /// Cancelled events intentionally produce a deletion tombstone with provenance but no event
    /// fields. Unsupported event types are ignored before the canonical payload is constructed.
    /// </remarks>
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
            Description = ReadStringOrNull(element, "description"),
            From = ReadEventDateTime(element, "start"),
            To = ReadEventDateTime(element, "end"),
            IsAllDay = IsAllDayEvent(element, "start"),
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

        return ContentHashHelper.WithComputedHash(item);
    }

    /// <summary>
    /// Creates the writable subset of a canonical event understood by Google Calendar.
    /// </summary>
    /// <remarks>
    /// Organizer and attendee fields are omitted so creates become private mailbox-owned copies
    /// and updates preserve participant data already held by the destination. Recurrence rules
    /// are split into the repeated-string shape required by the API. The provider-supplied
    /// iCalendar UID is intentionally omitted: Google treats it as an import upsert key, and it
    /// cannot be changed reliably after creation.
    /// </remarks>
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

        if (item.IsAllDay)
        {
            body["start"] = CreateAllDayDateNode(item.From);
            body["end"] = CreateAllDayDateNode(item.To);
        }

        if (!string.IsNullOrWhiteSpace(item.RecurrencePattern))
        {
            body["recurrence"] = new JsonArray(item.RecurrencePattern
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(rule => (JsonNode?)rule)
                .ToArray());
        }

        return body;
    }

    /// <summary>
    /// Reads and joins the recurrence rules exposed by Google Calendar.
    /// </summary>
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
            .Select(NormalizeRecurrenceRule)
            .ToArray();

        return rules.Length == 0 ? null : string.Join('\n', rules);
    }

    /// <summary>
    /// Converts Google's RRULE text to the canonical property order used by Kagami.
    /// </summary>
    /// <remarks>
    /// RRULE property order has no semantic meaning, but it does affect the canonical content
    /// hash. This translation belongs in the provider connector: the canonical event remains a
    /// passive DTO, and imported JSON is already expected to contain canonical values.
    /// </remarks>
    private static string NormalizeRecurrenceRule(string rule)
    {
        const string recurrenceRulePrefix = "RRULE:";
        if (!rule.StartsWith(recurrenceRulePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return rule;
        }

        string[] properties = rule[recurrenceRulePrefix.Length..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return $"{recurrenceRulePrefix}{string.Join(';', properties.OrderBy(GetRecurrencePropertyOrder).ThenBy(GetRecurrencePropertyName, StringComparer.Ordinal).ThenBy(property => property, StringComparer.Ordinal))}";
    }

    private static int GetRecurrencePropertyOrder(string property) => GetRecurrencePropertyName(property) switch
    {
        "FREQ" => 0,
        "UNTIL" or "COUNT" => 1,
        "INTERVAL" => 2,
        var name when name.StartsWith("BY", StringComparison.Ordinal) => 3,
        "WKST" => 4,
        _ => 5,
    };

    private static string GetRecurrencePropertyName(string property) =>
        property.Split('=', 2)[0].Trim().ToUpperInvariant();

    /// <summary>
    /// Chooses the most useful location representation available in a Google event.
    /// </summary>
    /// <remarks>
    /// A literal location wins, followed by the Hangouts link and finally a video conference
    /// entry point. This keeps remote meeting links available when no display location exists.
    /// </remarks>
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

    /// <summary>
    /// Converts the Google attendee array into canonical participants.
    /// </summary>
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

    /// <summary>
    /// Reads one participant object from a provider property such as organizer.
    /// </summary>
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

    /// <summary>
    /// Reads either a timed or all-day Google event value as UTC.
    /// </summary>
    /// <remarks>
    /// All-day values have no time-zone component, so they are normalized to midnight UTC to keep
    /// the canonical model deterministic across machines.
    /// </remarks>
    private static DateTimeOffset ReadEventDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var dateNode))
        {
            return DateTimeOffset.MinValue;
        }

        string? dateTime = ReadString(dateNode, "dateTime");
        if (DateTimeOffset.TryParse(dateTime, out var parsedDateTime))
        {
            return ToSecondPrecision(parsedDateTime.ToUniversalTime());
        }

        string? date = ReadString(dateNode, "date");
        if (DateOnly.TryParse(date, out var parsedDate))
        {
            return new DateTimeOffset(parsedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Reduces a timestamp to the second precision supported by both calendar providers.
    /// </summary>
    private static DateTimeOffset ToSecondPrecision(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>
    /// Creates the UTC date-time object used in writable Google event payloads.
    /// </summary>
    private static JsonObject CreateEventDateTimeNode(DateTimeOffset value) =>
        new()
        {
            ["dateTime"] = value.ToUniversalTime().ToString("o"),
            ["timeZone"] = "UTC",
        };

    /// <summary>
    /// Creates the date-only object used in writable Google all-day event payloads.
    /// </summary>
    private static JsonObject CreateAllDayDateNode(DateTimeOffset value) =>
        new()
        {
            ["date"] = value.UtcDateTime.ToString("yyyy-MM-dd"),
        };

    /// <summary>
    /// Determines whether a Google event date value represents an all-day event.
    /// </summary>
    private static bool IsAllDayEvent(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var node) && node.TryGetProperty("date", out _);

    /// <summary>
    /// Creates an authenticated Google Calendar request.
    /// </summary>
    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        string token = await credential.GetAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Sends a request and parses a successful JSON response.
    /// </summary>
    private async Task<JsonDocument> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Converts a failed Google response into an exception containing the provider detail.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // DELETE is idempotent for an already-removed Google resource. A 410 Gone therefore
        // means the requested end state already holds, unlike a 410 from another operation.
        if (response.RequestMessage?.Method == HttpMethod.Delete
            && response.StatusCode == HttpStatusCode.Gone)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Google Calendar API request failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
    }

    /// <summary>
    /// Serializes a JSON node as an HTTP JSON request body.
    /// </summary>
    private static StringContent CreateJsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>
    /// Reads a nullable string property without treating missing and explicit null values differently.
    /// </summary>
    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    /// <summary>
    /// Reads a nullable string property and treats an empty value as absent.
    /// </summary>
    private static string? ReadStringOrNull(JsonElement element, string propertyName) =>
        ReadString(element, propertyName) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Calculates the start boundary for a full calendar read.
    /// </summary>
    /// <remarks>
    /// The boundary is rounded to the first day of the month after the one-year lookback month,
    /// matching the connector's historical full-load contract rather than the exact current time.
    /// </remarks>
    private static string GetFullLoadTimeMin(DateTimeOffset referenceTime)
    {
        var oneYearAgo = referenceTime.ToUniversalTime().AddMonths(-12);
        var firstBeginningOfMonthAfterOneYearAgo = new DateTimeOffset(oneYearAgo.Year, oneYearAgo.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return firstBeginningOfMonthAfterOneYearAgo.AddMonths(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    /// <summary>
    /// Reads a provider timestamp and returns null when it cannot be parsed.
    /// </summary>
    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Resolves the configured Google Calendar resource path.
    /// </summary>
    private static string GetCollectionPath(string endpointName, EndpointOptions endpoint)
    {
        string calendarId = endpoint.Properties.GetOptionalValue("calendarId") ?? "primary";
        return $"{GraphBaseUri}/calendars/{Uri.EscapeDataString(calendarId)}/events";
    }

    /// <summary>
    /// Deserializes the connector's persisted Google cursor.
    /// </summary>
    private static GoogleCursor ParseCursor(string cursor)
    {
        using var document = JsonDocument.Parse(cursor);
        var root = document.RootElement;
        return new GoogleCursor(
            root.TryGetProperty("syncToken", out var syncTokenElement) ? syncTokenElement.GetString() : null,
            root.TryGetProperty("pageToken", out var pageTokenElement) ? pageTokenElement.GetString() : null);
    }

    /// <summary>
    /// Serializes a Google page or sync token into the connector's persisted cursor format.
    /// </summary>
    private static string SerializeCursor(GoogleCursor cursor) =>
        new JsonObject
        {
            ["syncToken"] = cursor.SyncToken,
            ["pageToken"] = cursor.PageToken,
        }.ToJsonString();

    private sealed record GoogleCursor(string? SyncToken, string? PageToken);

    private sealed record ConnectorPage(IReadOnlyList<CanonicalEvent> Items, string? Cursor, bool HasMore);
}
