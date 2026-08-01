using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Synchronizes Microsoft Graph events with silent write operations.
/// </summary>
public class MicrosoftEventsConnector(HttpClient httpClient, string endpointName, EndpointOptions endpoint, MicrosoftClientCredential credential, ILogger<MicrosoftEventsConnector> logger) : IConnector<CanonicalEvent>
{
    private const string MicrosoftScope = "https://graph.microsoft.com/.default";
    private const string GraphBaseUri = "https://graph.microsoft.com/v1.0";
    // Request immutable ids to avoid Graph returning a new id if an event is moved.
    private const string ImmutableIdPreference = "IdType=\"ImmutableId\"";
    private const int PageSize = 100;
    private const string SelectFields = "id,subject,body,start,end,isAllDay,location,organizer,attendees,responseStatus,isOrganizer,recurrence,iCalUId,lastModifiedDateTime,changeKey";
    private readonly string collectionPath = GetCollectionPath(endpointName, endpoint);

    /// <inheritdoc/>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.MicrosoftEvents,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = true,
    };

    /// <inheritdoc/>
    public string EndpointName { get; } = endpointName;

    /// <inheritdoc/>
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Authenticating to {Endpoint}...", EndpointName);
        _ = await credential.TokenCredential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
        logger.LogInformation("Authenticated to {Endpoint}.", EndpointName);
    }

    /// <inheritdoc/>
    public async Task<ItemSet<CanonicalEvent>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        bool usingSavedCursor = cursor is not null;
        string requestUri = cursor
            ?? $"{collectionPath}/delta?$select={Uri.EscapeDataString(SelectFields)}&startDateTime={Uri.EscapeDataString(GetFullLoadStartDateTime(DateTimeOffset.UtcNow))}";
        List<CanonicalEvent> items = [];
        string? finalCursor = cursor;

        while (true)
        {
            var page = await GetCursorPageAsync(requestUri, usingSavedCursor, cancellationToken);
            items.AddRange(page.Items);
            finalCursor = page.Cursor;
            if (!page.HasMore)
            {
                break;
            }

            requestUri = page.Cursor ?? throw new InvalidOperationException("Microsoft calendar connector returned HasMore=true without a cursor.");
        }

        // A provider can repeat an item across pages; retain its final observation, including a later deletion.
        items = items.GroupBy(static item => item.Provenance.ProviderId, StringComparer.Ordinal).Select(static group => group.Last()).ToList();

        return new ItemSet<CanonicalEvent>(items, finalCursor);
    }

    /// <summary>
    /// Reads and maps one Microsoft Graph delta page, including deletion tombstones.
    /// </summary>
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    private async Task<CursorItemsPage> GetCursorPageAsync(string requestUri, bool usingSavedCursor, CancellationToken cancellationToken)
    {
        // Delta pages contain lightweight change records, so the request asks Graph for the
        // connector page size and keeps the saved-cursor flag for expired-cursor detection.
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={PageSize}");
        using var document = await SendForJsonAsync(request, cancellationToken, treatGoneAsExpiredCursor: usingSavedCursor);

        List<CanonicalEvent> items = [];
        if (document.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            // Graph represents removals with @removed instead of a normal event body; preserve
            // those ids as canonical tombstones so the sync planner can delete the counterpart.
            foreach (var element in values.EnumerateArray())
            {
                string? id = ReadString(element, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (element.TryGetProperty("@removed", out _))
                {
                    items.Add(new CanonicalEvent
                    {
                        IsDeleted = true,
                        Provenance =
                        {
                            ProviderId = id,
                            LastModified = ReadDateTimeOffset(element, "lastModifiedDateTime"),
                        },
                    });
                    continue;
                }

                // Ordinary changes are filtered by attendee relevance after full conversion,
                // because the response and organizer metadata live in the canonical item.
                var item = ConvertEvent(element, EndpointName);
                if (item is not null && ShouldSync(item))
                {
                    items.Add(item);
                }
            }
        }

        // A next link means traversal is incomplete; the delta link is durable only on the final
        // page and must not be mistaken for an intermediate continuation URL.
        string? nextLink = ReadString(document.RootElement, "@odata.nextLink");
        string? deltaLink = ReadString(document.RootElement, "@odata.deltaLink");
        return new CursorItemsPage(items, nextLink ?? deltaLink, nextLink is not null);
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}?$select={Uri.EscapeDataString(SelectFields)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var item = ConvertEvent(document.RootElement, EndpointName);
        return item is not null && ShouldSync(item) ? item : null;
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent> CreateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}?sendNotifications=false";
        using var request = await CreateRequestAsync(HttpMethod.Post, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(item));
        using var document = await SendForJsonAsync(request, cancellationToken);
        string id = ReadString(document.RootElement, "id")
            ?? throw new InvalidOperationException("Microsoft calendar create returned no event id.");
        return await GetItemAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft calendar create succeeded but the created event could not be reloaded.");
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent> UpdateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(item.Provenance.ProviderId)}?sendNotifications=false";
        using var request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(item));
        if (!string.IsNullOrWhiteSpace(item.Provenance.Version))
        {
            request.Headers.TryAddWithoutValidation("If-Match", item.Provenance.Version);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await GetItemAsync(item.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft calendar update succeeded but the updated event could not be reloaded.");
    }

    /// <inheritdoc/>
    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}?sendNotifications=false";
        using var request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>
    /// Determines whether Microsoft event participation makes an event relevant to this account.
    /// </summary>
    private static bool ShouldSync(CanonicalEvent item)
    {
        // Organizer events remain in scope regardless of the attendee response value.
        if (item.Metadata.TryGetValue("microsoft.isOrganizer", out string? isOrganizerRaw)
            && bool.TryParse(isOrganizerRaw, out bool isOrganizer)
            && isOrganizer)
        {
            return true;
        }

        // For invitations, keep only accepted or tentative responses and organizer records.
        string? response = item.Metadata.GetValueOrDefault("microsoft.responseStatus");
        return response is "organizer" or "accepted" or "tentativelyAccepted";
    }

    /// <summary>
    /// Converts a Microsoft Graph event payload into a canonical event and preserves sync metadata.
    /// </summary>
    internal static CanonicalEvent? ConvertEvent(JsonElement element, string endpointName)
    {
        // Graph change records without an id cannot be correlated with a persisted provider item.
        string? id = ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Keep provider-specific fields in metadata so filtering can happen after conversion without
        // expanding the provider-neutral event model with Graph-only concepts.
        var item = new CanonicalEvent
        {
            Title = ReadString(element, "subject") ?? string.Empty,
            Description = element.TryGetProperty("body", out var bodyNode) ? ReadStringOrNull(bodyNode, "content") : null,
            From = IsAllDayEvent(element) ? ReadAllDayDate(element, "start") : ReadDateTimeTimeZone(element, "start"),
            To = IsAllDayEvent(element) ? ReadAllDayDate(element, "end") : ReadDateTimeTimeZone(element, "end"),
            IsAllDay = IsAllDayEvent(element),
            Location = element.TryGetProperty("location", out var locationNode) ? ReadStringOrNull(locationNode, "displayName") : null,
            Organizer = ReadOrganizer(element),
            Attendees = ReadAttendees(element),
            RecurrencePattern = element.TryGetProperty("recurrence", out var recurrenceNode)
                ? DeserializeRecurrence(recurrenceNode)
                : null,
            ICalUid = ReadString(element, "iCalUId"),
            Provenance =
            {
                ProviderId = id,
                EndpointName = endpointName,
                Version = ReadString(element, "@odata.etag") ?? ReadString(element, "changeKey"),
                LastModified = ReadDateTimeOffset(element, "lastModifiedDateTime"),
            },
        };

        // Organizer and response metadata are deliberately captured even when a caller later filters
        // the event, because the same conversion path serves both item reads and delta pages.
        bool isOrganizer = element.TryGetProperty("isOrganizer", out var isOrganizerNode) && isOrganizerNode.ValueKind == JsonValueKind.True;
        item.Metadata["microsoft.isOrganizer"] = isOrganizer.ToString();
        string? response = element.TryGetProperty("responseStatus", out var responseStatusNode)
            ? ReadString(responseStatusNode, "response")
            : null;
        if (!string.IsNullOrWhiteSpace(response))
        {
            item.Metadata["microsoft.responseStatus"] = response;
        }

        string? changeKey = ReadString(element, "changeKey");
        if (!string.IsNullOrWhiteSpace(changeKey))
        {
            item.Metadata["microsoft.changeKey"] = changeKey;
        }

        return ContentHashHelper.WithComputedHash(item);
    }

    /// <summary>
    /// Builds the Microsoft Graph writable event payload from a canonical event.
    /// </summary>
    /// <remarks>
    /// Organizer, attendee, and provider-supplied iCalendar UID fields are intentionally omitted.
    /// Microsoft assigns the UID, treats it as immutable, and does not reliably honor a supplied
    /// value; participant fields are preserved on the destination rather than written by this
    /// connector.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Calendar writable payload uses known JsonNode shapes.")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "Calendar writable payload uses known JsonNode shapes.")]
    internal static JsonObject BuildWritableEvent(CanonicalEvent item)
    {
        // Graph requires nested body, start, end, and location objects even when some values are empty.
        JsonObject body = new()
        {
            ["subject"] = item.Title,
            ["body"] = new JsonObject
            {
                ["contentType"] = "html",
                ["content"] = item.Description ?? string.Empty,
            },
            ["start"] = CreateDateTimeTimeZoneNode(item.From),
            ["end"] = CreateDateTimeTimeZoneNode(item.To),
            ["location"] = new JsonObject
            {
                ["displayName"] = item.Location,
            },
        };

        if (item.IsAllDay)
        {
            body["isAllDay"] = true;
        }

        if (item.RecurrencePattern is not null
            && SerializeRecurrence(item.RecurrencePattern, item.From) is JsonObject recurrenceNode)
        {
            body["recurrence"] = recurrenceNode;
        }

        return body;
    }

    internal static string? DeserializeRecurrence(JsonElement recurrence)
    {
        if (recurrence.ValueKind != JsonValueKind.Object
            || !recurrence.TryGetProperty("pattern", out var pattern)
            || !pattern.TryGetProperty("type", out var typeNode))
        {
            return null;
        }

        string? type = typeNode.GetString();
        List<string> parts = [];
        switch (type)
        {
            case "daily":
                parts.Add("FREQ=DAILY");
                break;
            case "weekly" when ReadStringArray(pattern, "daysOfWeek") is { Count: > 0 } days:
                parts.Add("FREQ=WEEKLY");
                parts.Add($"BYDAY={string.Join(',', days.Select(ToIcalDay))}");
                break;
            case "absoluteMonthly" when ReadPositiveInt(pattern, "dayOfMonth") is int dayOfMonth:
                parts.Add("FREQ=MONTHLY");
                parts.Add($"BYMONTHDAY={dayOfMonth}");
                break;
            case "relativeMonthly" when ReadStringArray(pattern, "daysOfWeek") is { Count: 1 } relativeDays
                && ReadString(pattern, "index") is string index:
                parts.Add("FREQ=MONTHLY");
                parts.Add($"BYDAY={ToIcalIndex(index)}{ToIcalDay(relativeDays[0])}");
                break;
            case "absoluteYearly" when ReadPositiveInt(pattern, "dayOfMonth") is int yearlyDay
                && ReadPositiveInt(pattern, "month") is int yearlyMonth:
                parts.Add("FREQ=YEARLY");
                parts.Add($"BYMONTH={yearlyMonth}");
                parts.Add($"BYMONTHDAY={yearlyDay}");
                break;
            case "relativeYearly" when ReadStringArray(pattern, "daysOfWeek") is { Count: 1 } yearlyDays
                && ReadString(pattern, "index") is string yearlyIndex
                && ReadPositiveInt(pattern, "month") is int relativeYearlyMonth:
                parts.Add("FREQ=YEARLY");
                parts.Add($"BYMONTH={relativeYearlyMonth}");
                parts.Add($"BYDAY={ToIcalIndex(yearlyIndex)}{ToIcalDay(yearlyDays[0])}");
                break;
            default:
                return null;
        }

        if (ReadPositiveInt(pattern, "interval") is int interval && interval != 1)
        {
            parts.Add($"INTERVAL={interval}");
        }

        if (recurrence.TryGetProperty("range", out var range)
            && range.ValueKind == JsonValueKind.Object)
        {
            switch (ReadString(range, "type"))
            {
                case "numbered" when ReadPositiveInt(range, "numberOfOccurrences") is int count:
                    parts.Add($"COUNT={count}");
                    break;
                case "endDate" when DateOnly.TryParseExact(ReadString(range, "endDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate):
                    parts.Add($"UNTIL={endDate:yyyyMMdd}");
                    break;
                case "noEnd":
                    break;
                default:
                    return null;
            }
        }

        return NormalizeRecurrenceRule($"RRULE:{string.Join(';', parts)}");
    }

    /// <summary>
    /// Emits the Microsoft recurrence translation in the canonical RRULE property order.
    /// </summary>
    /// <remarks>
    /// Microsoft and Google can describe the same recurrence with different property ordering.
    /// The connector performs this provider-to-canonical translation before hashing, rather than
    /// making the canonical event model rewrite values supplied by callers or local imports.
    /// </remarks>
    private static string NormalizeRecurrenceRule(string rule)
    {
        const string recurrenceRulePrefix = "RRULE:";
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

    internal static JsonObject? SerializeRecurrence(string pattern, DateTimeOffset start)
    {
        if (!pattern.Trim().StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = pattern.Trim()[6..].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1], StringComparer.Ordinal);
        if (!values.TryGetValue("FREQ", out string? frequency))
        {
            return null;
        }

        int interval = 1;
        if (values.TryGetValue("INTERVAL", out string? intervalValue)
            && (!int.TryParse(intervalValue, CultureInfo.InvariantCulture, out interval) || interval <= 0))
        {
            return null;
        }

        JsonObject graphPattern = new()
        {
            ["interval"] = interval,
        };
        switch (frequency.ToUpperInvariant())
        {
            case "DAILY":
                graphPattern["type"] = "daily";
                break;
            case "WEEKLY" when TryGetDays(values, out string[]? weeklyDays):
                graphPattern["type"] = "weekly";
                graphPattern["daysOfWeek"] = new JsonArray(weeklyDays.Select(day => (JsonNode?)ToGraphDay(day)).ToArray());
                break;
            case "MONTHLY" when values.TryGetValue("BYMONTHDAY", out string? monthlyDay)
                && int.TryParse(monthlyDay, CultureInfo.InvariantCulture, out int parsedMonthlyDay)
                && parsedMonthlyDay is >= 1 and <= 31:
                graphPattern["type"] = "absoluteMonthly";
                graphPattern["dayOfMonth"] = parsedMonthlyDay;
                break;
            default:
                return null;
        }

        JsonObject range = new()
        {
            ["type"] = "noEnd",
            ["startDate"] = start.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (values.TryGetValue("COUNT", out string? count)
            && int.TryParse(count, CultureInfo.InvariantCulture, out int occurrences)
            && occurrences > 0)
        {
            range["type"] = "numbered";
            range["numberOfOccurrences"] = occurrences;
        }
        else if (values.TryGetValue("UNTIL", out string? until)
            && DateOnly.TryParseExact(until, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            range["type"] = "endDate";
            range["endDate"] = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return new JsonObject
        {
            ["pattern"] = graphPattern,
            ["range"] = range,
        };
    }

    private static List<string>? ReadStringArray(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null).Cast<string>().ToList()
            : null;

    private static int? ReadPositiveInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out int value)
            && value > 0
                ? value
                : null;

    private static string ToIcalDay(string day) => day.ToLowerInvariant() switch
    {
        "monday" => "MO",
        "tuesday" => "TU",
        "wednesday" => "WE",
        "thursday" => "TH",
        "friday" => "FR",
        "saturday" => "SA",
        "sunday" => "SU",
        _ => throw new FormatException($"Unsupported Microsoft recurrence day '{day}'."),
    };

    private static string ToIcalIndex(string index) => index.ToLowerInvariant() switch
    {
        "first" => "1",
        "second" => "2",
        "third" => "3",
        "fourth" => "4",
        "last" => "-1",
        _ => throw new FormatException($"Unsupported Microsoft recurrence index '{index}'."),
    };

    private static bool TryGetDays(IReadOnlyDictionary<string, string> values, out string[] days)
    {
        days = values.TryGetValue("BYDAY", out string? value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return days.Length > 0 && days.All(day => day is "MO" or "TU" or "WE" or "TH" or "FR" or "SA" or "SU");
    }

    private static string ToGraphDay(string day) => day switch
    {
        "MO" => "monday",
        "TU" => "tuesday",
        "WE" => "wednesday",
        "TH" => "thursday",
        "FR" => "friday",
        "SA" => "saturday",
        "SU" => "sunday",
        _ => throw new FormatException($"Unsupported iCalendar recurrence day '{day}'."),
    };

    /// <summary>
    /// Reads the nested Microsoft organizer address into a canonical participant.
    /// </summary>
    private static CalendarEventParticipant? ReadOrganizer(JsonElement element)
    {
        if (!element.TryGetProperty("organizer", out var organizer)
            || !organizer.TryGetProperty("emailAddress", out var emailAddress))
        {
            return null;
        }

        return new CalendarEventParticipant
        {
            Name = ReadString(emailAddress, "name"),
            Email = ReadString(emailAddress, "address"),
        };
    }

    /// <summary>
    /// Reads Microsoft attendee addresses and their response values.
    /// </summary>
    private static List<CalendarEventParticipant> ReadAttendees(JsonElement element)
    {
        if (!element.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Ignore malformed attendee entries rather than manufacturing participants without addresses.
        List<CalendarEventParticipant> values = [];
        foreach (var attendee in attendees.EnumerateArray())
        {
            if (!attendee.TryGetProperty("emailAddress", out var emailAddress))
            {
                continue;
            }

            string? response = attendee.TryGetProperty("status", out var statusNode)
                ? ReadString(statusNode, "response")
                : null;

            values.Add(new CalendarEventParticipant
            {
                Name = ReadString(emailAddress, "name"),
                Email = ReadString(emailAddress, "address"),
                ResponseStatus = response,
            });
        }

        return values;
    }

    /// <summary>
    /// Converts a Microsoft dateTime/timeZone object to a UTC canonical timestamp.
    /// </summary>
    /// <remarks>
    /// Graph may return an explicit offset or a wall-clock value paired with an IANA or Windows
    /// zone identifier, so the parser handles both forms before falling back to UTC.
    /// </remarks>
    private static DateTimeOffset ReadDateTimeTimeZone(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node))
        {
            return DateTimeOffset.MinValue;
        }

        string? dateTime = ReadString(node, "dateTime");
        if (string.IsNullOrWhiteSpace(dateTime))
        {
            return DateTimeOffset.MinValue;
        }

        // An explicit offset is authoritative and must not be reinterpreted through the named zone.
        if (HasExplicitUtcOrOffset(dateTime)
            && DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedOffset))
        {
            return ToSecondPrecision(parsedOffset.ToUniversalTime());
        }

        if (DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDateTime))
        {
            // Unspecified preserves the provider wall-clock value while TimeZoneInfo applies the
            // correct daylight-saving rules during conversion.
            var unspecifiedDateTime = DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Unspecified);
            string? timeZoneId = ReadString(node, "timeZone");
            if (TryResolveTimeZone(timeZoneId, out var timeZone))
            {
                return ToSecondPrecision(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecifiedDateTime, timeZone), TimeSpan.Zero));
            }

            // Unknown zones are treated as UTC to keep the canonical model deterministic.
            return ToSecondPrecision(new DateTimeOffset(DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Utc)));
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Reduces a timestamp to the second precision supported by both calendar providers.
    /// </summary>
    private static DateTimeOffset ToSecondPrecision(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>
    /// Reads a Microsoft all-day event date without applying its time-zone value.
    /// </summary>
    private static DateTimeOffset ReadAllDayDate(JsonElement element, string propertyName)
    {
        string? dateTime = element.TryGetProperty(propertyName, out var node) ? ReadString(node, "dateTime") : null;
        return DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(DateOnly.FromDateTime(parsed).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Determines whether a Microsoft event represents an all-day event.
    /// </summary>
    private static bool IsAllDayEvent(JsonElement element) =>
        element.TryGetProperty("isAllDay", out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Determines whether a Microsoft date-time string carries its own UTC or numeric offset.
    /// </summary>
    private static bool HasExplicitUtcOrOffset(string dateTime)
    {
        int timeSeparatorIndex = dateTime.IndexOf('T');
        if (timeSeparatorIndex < 0)
        {
            return false;
        }

        return dateTime.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || dateTime.IndexOf('+', timeSeparatorIndex + 1) >= 0
            || dateTime.IndexOf('-', timeSeparatorIndex + 1) >= 0;
    }

    /// <summary>
    /// Resolves UTC, native, and cross-platform IANA/Windows time-zone identifiers.
    /// </summary>
    private static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        // Microsoft payloads commonly use UTC aliases or platform-specific identifiers.
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(timeZoneId, "Etc/UTC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(timeZoneId, "Etc/GMT", StringComparison.OrdinalIgnoreCase))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        // Prefer the host's native identifier before attempting cross-platform conversion.
        if (TryFindTimeZone(timeZoneId, out timeZone))
        {
            return true;
        }

        // These conversions allow Linux and Windows workers to consume the same Graph payload.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out string? windowsTimeZoneId)
            && TryFindTimeZone(windowsTimeZoneId, out timeZone))
        {
            return true;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out string? ianaTimeZoneId)
            && TryFindTimeZone(ianaTimeZoneId, out timeZone))
        {
            return true;
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>
    /// Looks up a system time zone while converting unavailable or invalid identifiers to UTC.
    /// </summary>
    private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        // Both exceptions indicate that the provider supplied a zone unavailable on this host.
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>
    /// Creates the UTC dateTime/timeZone object expected by Microsoft Graph writes.
    /// </summary>
    private static JsonObject CreateDateTimeTimeZoneNode(DateTimeOffset value) =>
        new()
        {
            ["dateTime"] = value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
            ["timeZone"] = "UTC",
        };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    /// <summary>
    /// Reads a nullable string property and treats an empty value as absent.
    /// </summary>
    private static string? ReadStringOrNull(JsonElement element, string propertyName) =>
        ReadString(element, propertyName) is { Length: > 0 } value ? value : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        var token = await credential.TokenCredential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("Prefer", ImmutableIdPreference);
        return request;
    }

    private async Task<JsonDocument> SendForJsonAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool treatGoneAsExpiredCursor = false)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (treatGoneAsExpiredCursor && response.StatusCode == HttpStatusCode.Gone)
        {
            throw new ExpiredCursorException("Microsoft Graph delta cursor expired.");
        }

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
        throw new InvalidOperationException($"Microsoft Graph calendar request failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
    }

    private static StringContent CreateJsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static string GetFullLoadStartDateTime(DateTimeOffset referenceTime)
    {
        var oneYearAgo = referenceTime.ToUniversalTime().AddMonths(-12);
        var firstBeginningOfMonthAfterOneYearAgo = new DateTimeOffset(oneYearAgo.Year, oneYearAgo.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return firstBeginningOfMonthAfterOneYearAgo.AddMonths(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private sealed record CursorItemsPage(IReadOnlyList<CanonicalEvent> Items, string? Cursor, bool HasMore);

    private static string GetCollectionPath(string endpointName, EndpointOptions endpoint)
    {
        string userId = endpoint.Properties.GetRequiredValue("userId", $"endpoint '{endpointName}'");
        string? calendarId = endpoint.Properties.GetOptionalValue("calendarId");
        return calendarId is null
            ? $"{GraphBaseUri}/users/{Uri.EscapeDataString(userId)}/events"
            : $"{GraphBaseUri}/users/{Uri.EscapeDataString(userId)}/calendars/{Uri.EscapeDataString(calendarId)}/events";
    }
}
