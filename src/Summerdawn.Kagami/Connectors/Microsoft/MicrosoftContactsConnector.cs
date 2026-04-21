
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors.Microsoft;

internal sealed class MicrosoftContactsConnector : IConnector
{
    private const string MicrosoftScope = "https://graph.microsoft.com/.default";

    // The beta endpoint is used because it exposes the `phones` collection, which supports
    // multiple phone numbers of the same type (e.g. two mobile numbers). The v1.0 endpoint
    // only has `mobilePhone` (a single string) and therefore loses any second mobile number.
    private const string GraphBaseUrl = "https://graph.microsoft.com/beta";

    private const string ContactSelectFields = "id,displayName,givenName,middleName,surname,emailAddresses,phones,businessPhones,homePhones,mobilePhone,companyName,jobTitle,personalNotes,birthday,categories,homeAddress,businessAddress,otherAddress,lastModifiedDateTime";

    private readonly HttpClient httpClient;
    private readonly TokenCredential credential;
    private readonly string collectionPath;

    public MicrosoftContactsConnector(
        HttpClient httpClient,
        string endpointName,
        EndpointOptions endpoint,
        MicrosoftClientCredential credential,
        ILogger<MicrosoftContactsConnector> logger)
    {
        _ = logger;
        this.httpClient = httpClient;
        this.credential = credential.TokenCredential;
        string userId = endpoint.Properties.GetRequiredValue("userId", $"endpoint '{endpointName}'");
        string? folderId = endpoint.Properties.GetOptionalValue("folderId");
        collectionPath = folderId is null
            ? $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}/contacts"
            : $"{GraphBaseUrl}/users/{Uri.EscapeDataString(userId)}/contactFolders/{Uri.EscapeDataString(folderId)}/contacts";
    }

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.MicrosoftContacts,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = false,
        SupportsRecurrence = false,
        SupportsContactPhotos = true,
        SupportsServerSideFiltering = true,
    };

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
    }

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        GetPageAsync($"{collectionPath}/delta?$select={Uri.EscapeDataString(ContactSelectFields)}", cancellationToken);

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
        GetPageAsync(cursor, cancellationToken);

    public async Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{collectionPath}/{Uri.EscapeDataString(id)}?$select={Uri.EscapeDataString(ContactSelectFields)}", cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var item = ConvertContact(document.RootElement);
        if (item is not null && !item.IsDeleted)
        {
            await PopulatePhotoAsync(item, id, cancellationToken);
        }

        return item;
    }

    public async Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, collectionPath, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableContact(GetContactPayload(item)));
        using var document = await SendForJsonAsync(request, cancellationToken);
        var created = ConvertContact(document.RootElement) ?? throw new InvalidOperationException("Microsoft Contacts create returned no payload.");
        await SyncPhotoAsync(created.SourceId, item, deleteWhenAbsent: false, cancellationToken);
        return await GetItemAsync(created.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Contacts create succeeded but the created item could not be reloaded.");
    }

    public async Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Patch, $"{collectionPath}/{Uri.EscapeDataString(item.SourceId)}", cancellationToken);
        request.Content = CreateJsonContent(BuildWritableContact(GetContactPayload(item)));
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            request.Headers.TryAddWithoutValidation("If-Match", item.Version);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await SyncPhotoAsync(item.SourceId, item, deleteWhenAbsent: true, cancellationToken);
        return await GetItemAsync(item.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Contacts update succeeded but the updated item could not be reloaded.");
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
                var item = ConvertContact(element);
                if (item is not null)
                {
                    if (!item.IsDeleted)
                    {
                        ContactPhotoLoader.Attach(item, ct => PopulatePhotoAsync(item, item.SourceId, ct));
                    }

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

    private static JsonObject BuildWritableContact(CanonicalContact contact)
    {
        return new JsonObject
        {
            ["givenName"] = contact.GivenName,
            ["middleName"] = contact.MiddleName,
            ["surname"] = contact.FamilyName,
            ["displayName"] = contact.DisplayName,
            ["emailAddresses"] = CreateArray(contact.Emails.Select(email => (JsonNode?)new JsonObject
            {
                ["name"] = contact.DisplayName,
                ["address"] = email.Address,
            })),
            // phones (beta) supports all types and multiple entries with the same type,
            // so all numbers are preserved (e.g. two mobile numbers).
            ["phones"] = CreateArray(contact.Phones.Select(phone => (JsonNode?)new JsonObject
            {
                ["number"] = phone.Number,
                ["type"] = MapToMicrosoftPhoneType(phone.Label),
            })),
            // Keep legacy v1.0 fields so contacts are readable by clients that only use v1.0.
            ["businessPhones"] = CreateStringArray(contact.Phones.Where(phone => string.Equals(phone.Label, "work", StringComparison.OrdinalIgnoreCase)).Select(phone => phone.Number)),
            ["homePhones"] = CreateStringArray(contact.Phones.Where(phone => string.Equals(phone.Label, "home", StringComparison.OrdinalIgnoreCase)).Select(phone => phone.Number)),
            ["mobilePhone"] = contact.Phones.FirstOrDefault(phone => string.Equals(phone.Label, "mobile", StringComparison.OrdinalIgnoreCase))?.Number,
            ["companyName"] = contact.Organization,
            ["jobTitle"] = contact.Title,
            ["personalNotes"] = contact.Notes,
            ["birthday"] = JsonValue.Create(contact.Birthday?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            ["categories"] = CreateStringArray(contact.Categories),
            ["homeAddress"] = ToMicrosoftAddress(contact.Addresses.FirstOrDefault(address => string.Equals(address.Label, "home", StringComparison.OrdinalIgnoreCase))),
            ["businessAddress"] = ToMicrosoftAddress(contact.Addresses.FirstOrDefault(address => string.Equals(address.Label, "work", StringComparison.OrdinalIgnoreCase))),
            ["otherAddress"] = ToMicrosoftAddress(contact.Addresses.FirstOrDefault(address => string.Equals(address.Label, "other", StringComparison.OrdinalIgnoreCase))),
        };
    }

    private static JsonObject? ToMicrosoftAddress(ContactAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["street"] = address.Street,
            ["city"] = address.City,
            ["state"] = address.State,
            ["postalCode"] = address.PostalCode,
            ["countryOrRegion"] = address.Country,
        };
    }

    private static CanonicalContact GetContactPayload(CanonicalItem item) =>
        item.Payload as CanonicalContact
        ?? throw new InvalidOperationException("Microsoft Contacts connector only supports CanonicalContact payloads.");

    private static JsonArray CreateArray(IEnumerable<JsonNode?> values)
    {
        JsonArray array = [];
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray CreateStringArray(IEnumerable<string> values)
    {
        JsonArray array = [];
        foreach (string value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        return array;
    }

    private static CanonicalItem? ConvertContact(JsonElement element)
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
                EntityType = EntityType.Contact,
                SourceId = id,
                IsDeleted = true,
            };
        }

        CanonicalContact contact = new()
        {
            GivenName = ReadString(element, "givenName"),
            MiddleName = ReadString(element, "middleName"),
            FamilyName = ReadString(element, "surname"),
            DisplayName = ReadString(element, "displayName") ?? string.Empty,
            Organization = ReadString(element, "companyName"),
            Title = ReadString(element, "jobTitle"),
            Notes = ReadString(element, "personalNotes"),
            Birthday = ReadDateOnly(element, "birthday"),
            LastModified = ReadDateTimeOffset(element, "lastModifiedDateTime"),
            Categories = ReadStringArray(element, "categories"),
        };

        if (element.TryGetProperty("emailAddresses", out var emailAddresses))
        {
            foreach (var email in emailAddresses.EnumerateArray())
            {
                string? address = ReadString(email, "address");
                if (!string.IsNullOrWhiteSpace(address))
                {
                    contact.Emails.Add(new ContactEmail
                    {
                        Address = address,
                        Label = "other",
                    });
                }
            }
        }

        // Prefer the beta `phones` collection: it supports every phone type and allows multiple
        // entries with the same type (e.g. two mobile numbers).  Fall back to the v1.0
        // individual fields (`businessPhones`, `homePhones`, `mobilePhone`) only when the
        // beta property is absent so that contacts fetched without the beta endpoint still work.
        if (element.TryGetProperty("phones", out var phonesElement) && phonesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var phone in phonesElement.EnumerateArray())
            {
                string? number = ReadString(phone, "number");
                if (!string.IsNullOrWhiteSpace(number))
                {
                    contact.Phones.Add(new ContactPhone
                    {
                        Number = number,
                        Label = MapFromMicrosoftPhoneType(ReadString(phone, "type")),
                    });
                }
            }
        }
        else
        {
            AddPhones(contact.Phones, element, "businessPhones", "work");
            AddPhones(contact.Phones, element, "homePhones", "home");
            string? mobilePhone = ReadString(element, "mobilePhone");
            if (!string.IsNullOrWhiteSpace(mobilePhone))
            {
                contact.Phones.Add(new ContactPhone { Number = mobilePhone, Label = "mobile" });
            }
        }

        AddAddress(contact.Addresses, element, "homeAddress", "home");
        AddAddress(contact.Addresses, element, "businessAddress", "work");
        AddAddress(contact.Addresses, element, "otherAddress", "other");

        return CanonicalItemSerializer.WithComputedHash(new CanonicalItem
        {
            EntityType = EntityType.Contact,
            Payload = contact,
            SourceId = id,
            Version = ReadString(element, "@odata.etag"),
            Metadata = [],
        });
    }

    private async Task PopulatePhotoAsync(CanonicalItem item, string id, CancellationToken cancellationToken)
    {
        var photo = await DownloadPhotoAsync(id, cancellationToken);
        if (photo is null)
        {
            ContactPhotoMetadata.SetNoPhoto(item);
            return;
        }

        ContactPhotoMetadata.SetPhoto(item, photo.Value.photoBytes, photo.Value.contentType);
    }

    private async Task<(byte[] photoBytes, string contentType)?> DownloadPhotoAsync(string id, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{collectionPath}/{Uri.EscapeDataString(id)}/photo/$value", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        byte[] photoBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (photoBytes.Length == 0)
        {
            return null;
        }

        string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (photoBytes, contentType);
    }

    private async Task SyncPhotoAsync(string id, CanonicalItem item, bool deleteWhenAbsent, CancellationToken cancellationToken)
    {
        if (ContactPhotoMetadata.TryGetPhoto(item, out byte[] photoBytes, out string contentType))
        {
            using var request = await CreateRequestAsync(HttpMethod.Put, $"{collectionPath}/{Uri.EscapeDataString(id)}/photo/$value", cancellationToken);
            request.Content = new ByteArrayContent(photoBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return;
        }

        if (!deleteWhenAbsent || !ContactPhotoMetadata.HasKnownAbsence(item))
        {
            return;
        }

        using var deleteRequest = await CreateRequestAsync(HttpMethod.Delete, $"{collectionPath}/{Uri.EscapeDataString(id)}/photo/$value", cancellationToken);
        using var deleteResponse = await httpClient.SendAsync(deleteRequest, cancellationToken);
        if (deleteResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(deleteResponse, cancellationToken);
    }

    private static void AddPhones(ICollection<ContactPhone> phones, JsonElement element, string propertyName, string label)
    {
        if (!element.TryGetProperty(propertyName, out var values))
        {
            return;
        }

        foreach (var phone in values.EnumerateArray())
        {
            string? value = phone.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                phones.Add(new ContactPhone { Number = value, Label = label });
            }
        }
    }

    /// <summary>
    /// Maps a Microsoft Graph beta <c>phoneType</c> value to a canonical phone label.
    /// The beta API uses "business" where the canonical model uses "work"; everything else
    /// is passed through as-is (lowercased).
    /// </summary>
    internal static string MapFromMicrosoftPhoneType(string? microsoftType) =>
        microsoftType?.ToLowerInvariant() switch
        {
            "business" => "work",
            string other => other,
            null => "other",
        };

    /// <summary>
    /// Maps a canonical phone label to a Microsoft Graph beta <c>phoneType</c> value.
    /// The canonical model uses "work" where the beta API uses "business"; everything else
    /// is passed through as-is (lowercased).
    /// </summary>
    internal static string MapToMicrosoftPhoneType(string? canonicalLabel) =>
        canonicalLabel?.ToLowerInvariant() switch
        {
            "work" => "business",
            string other => other,
            null => "other",
        };

    private static void AddAddress(ICollection<ContactAddress> addresses, JsonElement element, string propertyName, string label)
    {
        if (!element.TryGetProperty(propertyName, out var addressElement) || addressElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        addresses.Add(new ContactAddress
        {
            Label = label,
            Street = ReadString(addressElement, "street"),
            City = ReadString(addressElement, "city"),
            State = ReadString(addressElement, "state"),
            PostalCode = ReadString(addressElement, "postalCode"),
            Country = ReadString(addressElement, "countryOrRegion"),
        });
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var dateTimeOffset) ? dateTimeOffset : null;
    }

    private static DateOnly? ReadDateOnly(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        if (DateTimeOffset.TryParse(value, out var dateTimeOffset))
        {
            return DateOnly.FromDateTime(dateTimeOffset.UtcDateTime);
        }

        return null;
    }
}
