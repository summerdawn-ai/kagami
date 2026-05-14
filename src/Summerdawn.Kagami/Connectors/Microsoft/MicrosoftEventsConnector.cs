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
public class MicrosoftEventsConnector(HttpClient httpClient, string endpointName, EndpointOptions endpoint, MicrosoftClientCredential credential) : IConnector<CanonicalEvent>
{
    private const string MicrosoftScope = "https://graph.microsoft.com/.default";
    private const string GraphBaseUri = "https://graph.microsoft.com/v1.0";
    private const int PageSize = 100;
    private const string SelectFields = "id,subject,body,start,end,location,organizer,attendees,responseStatus,isOrganizer,recurrence,iCalUId,lastModifiedDateTime,changeKey";
    private readonly string collectionPath = GetCollectionPath(endpointName, endpoint);

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.MicrosoftCalendar,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = true,
    };

    public string EndpointName { get; } = endpointName;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.TokenCredential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
    }

    public async Task<ItemSet<CanonicalEvent>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        bool usingSavedCursor = cursor is not null;
        string requestUri = cursor
            ?? $"{collectionPath}/delta?$select={Uri.EscapeDataString(SelectFields)}&$top={PageSize}&startDateTime={Uri.EscapeDataString(GetFullLoadStartDateTime(DateTimeOffset.UtcNow))}";
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

        return new ItemSet<CanonicalEvent>(items, finalCursor);
    }

    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    private async Task<CursorItemsPage> GetCursorPageAsync(string requestUri, bool usingSavedCursor, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={PageSize}");
        using var document = await SendForJsonAsync(request, cancellationToken, treatGoneAsExpiredCursor: usingSavedCursor);

        List<CanonicalEvent> items = [];
        if (document.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
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

                var item = ConvertEvent(element, EndpointName);
                if (item is not null && ShouldSync(item))
                {
                    items.Add(item);
                }
            }
        }

        string? nextLink = ReadString(document.RootElement, "@odata.nextLink");
        string? deltaLink = ReadString(document.RootElement, "@odata.deltaLink");
        return new CursorItemsPage(items, nextLink ?? deltaLink, nextLink is not null);
    }

    public async Task<CanonicalEvent?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}?$select={Uri.EscapeDataString(SelectFields)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var item = ConvertEvent(document.RootElement, EndpointName);
        return item is not null && ShouldSync(item) ? item : null;
    }

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

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"{collectionPath}/{Uri.EscapeDataString(id)}?sendNotifications=false";
        using var request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static bool ShouldSync(CanonicalEvent item)
    {
        if (item.Metadata.TryGetValue("microsoft.isOrganizer", out string? isOrganizerRaw)
            && bool.TryParse(isOrganizerRaw, out bool isOrganizer)
            && isOrganizer)
        {
            return true;
        }

        string? response = item.Metadata.GetValueOrDefault("microsoft.responseStatus");
        return response is "accepted" or "tentativelyAccepted";
    }

    internal static CanonicalEvent? ConvertEvent(JsonElement element, string endpointName)
    {
        string? id = ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var item = new CanonicalEvent
        {
            Title = ReadString(element, "subject") ?? string.Empty,
            Description = element.TryGetProperty("body", out var bodyNode) ? ReadString(bodyNode, "content") : null,
            From = ReadDateTimeTimeZone(element, "start"),
            To = ReadDateTimeTimeZone(element, "end"),
            Location = element.TryGetProperty("location", out var locationNode) ? ReadString(locationNode, "displayName") : null,
            Organizer = ReadOrganizer(element),
            Attendees = ReadAttendees(element),
            RecurrencePattern = element.TryGetProperty("recurrence", out var recurrenceNode) && recurrenceNode.ValueKind != JsonValueKind.Null
                ? recurrenceNode.GetRawText()
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

        return item;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Calendar writable payload uses known JsonNode shapes.")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "Calendar writable payload uses known JsonNode shapes.")]
    internal static JsonObject BuildWritableEvent(CanonicalEvent item)
    {
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

        if (item.Organizer is not null && !string.IsNullOrWhiteSpace(item.Organizer.Email))
        {
            body["organizer"] = new JsonObject
            {
                ["emailAddress"] = new JsonObject
                {
                    ["address"] = item.Organizer.Email,
                    ["name"] = item.Organizer.Name,
                },
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
                    ["emailAddress"] = new JsonObject
                    {
                        ["address"] = attendee.Email,
                        ["name"] = attendee.Name,
                    },
                    ["type"] = "required",
                });
            }

            if (attendees.Count > 0)
            {
                body["attendees"] = attendees;
            }
        }

        if (!string.IsNullOrWhiteSpace(item.RecurrencePattern)
            && JsonNode.Parse(item.RecurrencePattern) is JsonNode recurrenceNode)
        {
            body["recurrence"] = recurrenceNode;
        }

        return body;
    }

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

    private static List<CalendarEventParticipant> ReadAttendees(JsonElement element)
    {
        if (!element.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

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

        if (HasExplicitUtcOrOffset(dateTime)
            && DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedOffset))
        {
            return parsedOffset.ToUniversalTime();
        }

        if (DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDateTime))
        {
            var unspecifiedDateTime = DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Unspecified);
            string? timeZoneId = ReadString(node, "timeZone");
            if (TryResolveTimeZone(timeZoneId, out var timeZone))
            {
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecifiedDateTime, timeZone), TimeSpan.Zero);
            }

            return new DateTimeOffset(DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Utc));
        }

        return DateTimeOffset.MinValue;
    }

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

    private static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(timeZoneId, "Etc/UTC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(timeZoneId, "Etc/GMT", StringComparison.OrdinalIgnoreCase))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        if (TryFindTimeZone(timeZoneId, out timeZone))
        {
            return true;
        }

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

    private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

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
