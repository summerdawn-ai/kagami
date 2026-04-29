
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors.Microsoft;

internal sealed class MicrosoftCalendarConnector : IConnector
{
    private const string MicrosoftScope = "https://graph.microsoft.com/.default";
    private const string EventSelectFields = "id,subject,bodyPreview,start,end,location,isAllDay,organizer,attendees,iCalUId,seriesMasterId,type,showAs,importance,lastModifiedDateTime";

    private readonly HttpClient httpClient;
    private readonly TokenCredential credential;
    private readonly string collectionPath;

    public MicrosoftCalendarConnector(
        HttpClient httpClient,
        string endpointName,
        EndpointOptions endpoint,
        MicrosoftClientCredential credential,
        ILogger<MicrosoftCalendarConnector> logger)
    {
        _ = logger;
        this.httpClient = httpClient;
        this.credential = credential.TokenCredential;
        string userId = endpoint.Properties.GetRequiredValue("userId", $"endpoint '{endpointName}'");
        string? calendarId = endpoint.Properties.GetOptionalValue("calendarId");
        collectionPath = calendarId is null
            ? $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}/events"
            : $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}/calendars/{Uri.EscapeDataString(calendarId)}/events";
    }

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.MicrosoftCalendar,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = false,
    };

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
    }

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        GetPageAsync($"{collectionPath}/delta?$select={Uri.EscapeDataString(EventSelectFields)}", cancellationToken);

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
        GetPageAsync(cursor, cancellationToken);

    public async Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{collectionPath}/{Uri.EscapeDataString(id)}?$select={Uri.EscapeDataString(EventSelectFields)}", cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        return ConvertEvent(document.RootElement);
    }

    public async Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, collectionPath, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(GetEventPayload(item)));
        using var document = await SendForJsonAsync(request, cancellationToken);
        var created = ConvertEvent(document.RootElement)
            ?? throw new InvalidOperationException("Microsoft Calendar create returned no payload.");
        return await GetItemAsync(created.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Calendar create succeeded but the created item could not be reloaded.");
    }

    public async Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Patch, $"{collectionPath}/{Uri.EscapeDataString(item.SourceId)}", cancellationToken);
        request.Content = CreateJsonContent(BuildWritableEvent(GetEventPayload(item)));
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            request.Headers.TryAddWithoutValidation("If-Match", item.Version);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await GetItemAsync(item.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Calendar update succeeded but the updated item could not be reloaded.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, $"{collectionPath}/{Uri.EscapeDataString(id)}", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<IncrementalPage> GetPageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);

        List<CanonicalItem> items = [];
        if (document.RootElement.TryGetProperty("value", out var values))
        {
            foreach (var element in values.EnumerateArray())
            {
                var item = ConvertEvent(element);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        string? nextLink = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement)
            ? nextLinkElement.GetString()
            : null;
        string? deltaLink = document.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement)
            ? deltaLinkElement.GetString()
            : null;

        return new IncrementalPage
        {
            Items = items,
            HasMore = nextLink is not null,
            NextCursor = nextLink ?? deltaLink,
        };
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string requestUri, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
        HttpRequestMessage request = new(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
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
        throw new InvalidOperationException($"Microsoft Graph request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static StringContent CreateJsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject BuildWritableEvent(CanonicalCalendarEvent calendarEvent)
    {
        JsonObject body = new()
        {
            ["subject"] = calendarEvent.Subject,
            ["start"] = new JsonObject
            {
                ["dateTime"] = calendarEvent.IsAllDay
                    ? calendarEvent.Start.UtcDateTime.ToString("yyyy-MM-dd")
                    : calendarEvent.Start.UtcDateTime.ToString("o"),
                ["timeZone"] = "UTC",
            },
            ["end"] = new JsonObject
            {
                ["dateTime"] = calendarEvent.IsAllDay
                    ? calendarEvent.End.UtcDateTime.ToString("yyyy-MM-dd")
                    : calendarEvent.End.UtcDateTime.ToString("o"),
                ["timeZone"] = "UTC",
            },
            ["isAllDay"] = calendarEvent.IsAllDay,
        };

        if (!string.IsNullOrWhiteSpace(calendarEvent.Body))
        {
            body["body"] = new JsonObject
            {
                ["contentType"] = "text",
                ["content"] = calendarEvent.Body,
            };
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
        {
            body["location"] = new JsonObject
            {
                ["displayName"] = calendarEvent.Location,
            };
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.ShowAs))
        {
            body["showAs"] = calendarEvent.ShowAs;
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.Importance))
        {
            body["importance"] = calendarEvent.Importance;
        }

        return body;
    }

    private static CanonicalCalendarEvent GetEventPayload(CanonicalItem item) =>
        item.Payload as CanonicalCalendarEvent
        ?? throw new InvalidOperationException("Microsoft Calendar connector only supports CanonicalCalendarEvent payloads.");

    private static CanonicalItem? ConvertEvent(JsonElement element)
    {
        string? id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (element.TryGetProperty("@removed", out _))
        {
            return new CanonicalItem
            {
                EntityType = EntityType.CalendarEvent,
                SourceId = id,
                IsDeleted = true,
            };
        }

        bool isAllDay = element.TryGetProperty("isAllDay", out var isAllDayElement)
            && isAllDayElement.ValueKind == JsonValueKind.True;

        CanonicalCalendarEvent calendarEvent = new()
        {
            Subject = ReadString(element, "subject") ?? string.Empty,
            Body = ReadString(element, "bodyPreview"),
            IsAllDay = isAllDay,
            Location = ReadLocationDisplayName(element),
            OrganizerEmail = ReadOrganizerEmail(element),
            ICalUid = ReadString(element, "iCalUId"),
            SeriesMasterId = ReadString(element, "seriesMasterId"),
            IsRecurring = element.TryGetProperty("type", out var typeElement)
                && (typeElement.GetString() == "occurrence" || typeElement.GetString() == "seriesMaster"),
            ShowAs = ReadString(element, "showAs"),
            Importance = ReadString(element, "importance"),
            LastModified = ReadDateTimeOffset(element, "lastModifiedDateTime"),
        };

        ReadEventDateTime(element, "start", isAllDay, out var start, out _);
        ReadEventDateTime(element, "end", isAllDay, out var end, out _);
        calendarEvent.Start = start;
        calendarEvent.End = end;

        if (element.TryGetProperty("attendees", out var attendees))
        {
            foreach (var attendee in attendees.EnumerateArray())
            {
                string? email = attendee.TryGetProperty("emailAddress", out var emailAddress)
                    ? ReadString(emailAddress, "address")
                    : null;
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
            Version = ReadString(element, "@odata.etag"),
            Metadata = [],
        });
    }

    private static void ReadEventDateTime(JsonElement element, string propertyName, bool isAllDay, out DateTimeOffset dateTime, out string? timeZone)
    {
        dateTime = default;
        timeZone = null;

        if (!element.TryGetProperty(propertyName, out var dtElement))
        {
            return;
        }

        timeZone = ReadString(dtElement, "timeZone");
        string? value = ReadString(dtElement, "dateTime");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (isAllDay && DateOnly.TryParse(value, out var dateOnly))
        {
            dateTime = new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
        else if (DateTimeOffset.TryParse(value, out var parsedDto))
        {
            dateTime = parsedDto;
        }
    }

    private static string? ReadLocationDisplayName(JsonElement element)
    {
        if (!element.TryGetProperty("location", out var location))
        {
            return null;
        }

        return ReadString(location, "displayName");
    }

    private static string? ReadOrganizerEmail(JsonElement element)
    {
        if (!element.TryGetProperty("organizer", out var organizer))
        {
            return null;
        }

        if (!organizer.TryGetProperty("emailAddress", out var emailAddress))
        {
            return null;
        }

        return ReadString(emailAddress, "address");
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var dateTimeOffset) ? dateTimeOffset : null;
    }
}
