using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

public sealed class MicrosoftContactsConnector(HttpClient httpClient, string endpointName, EndpointOptions endpoint, MicrosoftClientCredential credential) : IConnector<CanonicalContact>
{
    private const string MicrosoftScope = "https://graph.microsoft.com/.default";
    private const string GraphBaseUri = "https://graph.microsoft.com/v1.0";
    private const string ContactSelectFields = "id,displayName,givenName,middleName,surname,emailAddresses,businessPhones,homePhones,mobilePhone,companyName,jobTitle,personalNotes,birthday,categories,homeAddress,businessAddress,otherAddress,lastModifiedDateTime";
    private const string DeltaSelectFields = "id,lastModifiedDateTime";
    // Delta returns only lightweight metadata, so we can ask Graph for larger pages before hydrating the contacts in batches.
    private const int DeltaPageSize = 200;
    // Full pages are heavier so we keep it to 100 items.
    private const int AllPageSize = 100;
    // Microsoft Graph limits JSON batching to 20 requests, so 200-contact pages are hydrated in 20-item chunks.
    private const int MaxBatchSize = 20;

    // Additional Microsoft phone fields not available on the standard contact resource.
    // We intentionally only map phone-number labels that are supported on Microsoft for this connector.
    // Fax numbers are excluded. business2/home2 are excluded because the native arrays already handle multiple work/home numbers.
    private static readonly (string Label, string GraphId)[] ExtendedPhoneProperties =
    [
        ("other",     "String 0x3A1F"),
        ("pager",     "String 0x3A21"),
        ("radio",     "String 0x3A1D"),
        ("assistant", "String 0x3A2E"),
        ("main",      "String 0x3A57"),
    ];

    private static readonly string ExtendedPropertiesExpand =
        "$expand=singleValueExtendedProperties($filter=" +
        string.Join(" or ", ExtendedPhoneProperties.Select(p => $"id eq '{p.GraphId}'")) +
        ")";

    private readonly string collectionPath = GetCollectionPath(endpointName, endpoint);

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

    public string EndpointName { get; } = endpointName;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.TokenCredential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
    }

    public async Task<ItemSet<CanonicalContact>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        string requestUri = cursor ?? $"{collectionPath}/delta?$select={Uri.EscapeDataString(DeltaSelectFields)}";
        List<CanonicalContact> items = [];
        string? finalCursor = cursor;
        while (true)
        {
            var page = await GetCursorPageAsync(requestUri, cancellationToken);
            items.AddRange(page.Items);
            finalCursor = page.Cursor;
            if (!page.HasMore)
            {
                break;
            }

            requestUri = page.Cursor ?? throw new InvalidOperationException("Microsoft connector returned HasMore=true without a cursor.");
        }

        return new ItemSet<CanonicalContact>(items, finalCursor);
    }

    public async Task<IReadOnlyList<CanonicalContact>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        string? requestUri = $"{collectionPath}?$select={Uri.EscapeDataString(ContactSelectFields)}&{ExtendedPropertiesExpand}&$top={AllPageSize}";
        List<CanonicalContact> items = [];

        while (requestUri is not null)
        {
            var page = await GetAllItemsPageAsync(requestUri, cancellationToken);
            items.AddRange(page.Items);
            requestUri = page.NextLink;
        }

        return items;
    }

    public async Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string uri = $"{collectionPath}/{Uri.EscapeDataString(id)}?$select={Uri.EscapeDataString(ContactSelectFields)}&{ExtendedPropertiesExpand}";
        using var request = await CreateRequestAsync(HttpMethod.Get, uri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var contact = ConvertContact(document.RootElement);
        if (contact is not null && !contact.IsDeleted)
        {
            ApplyExtendedPhoneProperties(contact, document.RootElement);
            await PopulatePhotoAsync(contact, id, cancellationToken);
        }

        return contact;
    }

    public async Task<CanonicalContact> CreateItemAsync(CanonicalContact contact, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, collectionPath, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableContact(contact));
        using var document = await SendForJsonAsync(request, cancellationToken);
        var created = ConvertContact(document.RootElement) ?? throw new InvalidOperationException("Microsoft Contacts create returned no payload.");
        await SyncPhotoAsync(created.Provenance.ProviderId, contact, deleteWhenAbsent: false, cancellationToken);
        return await GetItemAsync(created.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Contacts create succeeded but the created contact could not be reloaded.");
    }

    public async Task<CanonicalContact> UpdateItemAsync(CanonicalContact contact, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Patch, $"{collectionPath}/{Uri.EscapeDataString(contact.Provenance.ProviderId)}", cancellationToken);
        request.Content = CreateJsonContent(BuildWritableContact(contact));
        if (!string.IsNullOrWhiteSpace(contact.Provenance.Version))
        {
            request.Headers.TryAddWithoutValidation("If-Match", contact.Provenance.Version);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await SyncPhotoAsync(contact.Provenance.ProviderId, contact, deleteWhenAbsent: true, cancellationToken);
        return await GetItemAsync(contact.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Microsoft Contacts update succeeded but the updated contact could not be reloaded.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Delete, $"{collectionPath}/{Uri.EscapeDataString(id)}", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    private async Task<CursorItemsPage> GetCursorPageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={DeltaPageSize}");
        using var document = await SendForJsonAsync(request, cancellationToken);

        List<CanonicalContact> items = [];
        List<string> idsToHydrate = [];
        if (document.RootElement.TryGetProperty("value", out var values))
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
                    items.Add(new CanonicalContact
                    {
                        IsDeleted = true,
                        Provenance =
                        {
                            ProviderId = id,
                            LastModified = ReadDateTimeOffset(element, "lastModifiedDateTime"),
                        }
                    });
                    continue;
                }

                idsToHydrate.Add(id);
            }
        }

        if (idsToHydrate.Count > 0)
        {
            items.AddRange(await BatchGetContactsAsync(idsToHydrate, cancellationToken));
        }

        string? nextLink = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement)
            ? nextLinkElement.GetString()
            : null;
        string? deltaLink = document.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement)
            ? deltaLinkElement.GetString()
            : null;

        return new CursorItemsPage(items, nextLink ?? deltaLink, nextLink is not null);
    }

    private async Task<AllItemsPage> GetAllItemsPageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);

        List<CanonicalContact> items = [];
        if (document.RootElement.TryGetProperty("value", out var values))
        {
            foreach (var element in values.EnumerateArray())
            {
                var contact = ConvertContact(element);
                if (contact is null || contact.IsDeleted)
                {
                    continue;
                }

                ApplyExtendedPhoneProperties(contact, element);
                ContactPhotoLoader.Attach(contact, ct => PopulatePhotoAsync(contact, contact.Provenance.ProviderId, ct));
                items.Add(contact);
            }
        }

        string? nextLink = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement)
            ? nextLinkElement.GetString()
            : null;

        return new AllItemsPage(items, nextLink);
    }

    /// <summary>
    /// Hydrates the metadata-only delta page by fetching the full contact payload only for the ids returned on that page.
    /// </summary>
    /// <remarks>
    /// The delta request can page up to 200 lightweight ids at a time, but Microsoft Graph JSON batching allows only 20 subrequests per batch,
    /// so the connector splits each page into smaller chunks and reassembles the hydrated results in page order.
    /// </remarks>
    private async Task<List<CanonicalContact>> BatchGetContactsAsync(List<string> ids, CancellationToken cancellationToken)
    {
        List<CanonicalContact> items = [];
        foreach (string[] chunk in ids.Chunk(MaxBatchSize))
        {
            items.AddRange(await BatchGetContactsChunkAsync(chunk, cancellationToken));
        }

        return items;
    }

    /// <summary>
    /// Sends one Microsoft Graph <c>$batch</c> request for a single chunk of contact ids and maps the responses back to canonical contacts.
    /// </summary>
    private async Task<List<CanonicalContact>> BatchGetContactsChunkAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"{GraphBaseUri}/$batch", cancellationToken);
        request.Content = CreateJsonContent(CreateBatchRequestBody(ids));
        using var document = await SendForJsonAsync(request, cancellationToken);

        Dictionary<string, JsonElement> responseById = [];
        if (document.RootElement.TryGetProperty("responses", out var responses))
        {
            foreach (var response in responses.EnumerateArray())
            {
                string? responseId = ReadString(response, "id");
                if (!string.IsNullOrWhiteSpace(responseId))
                {
                    responseById[responseId] = response.Clone();
                }
            }
        }

        List<CanonicalContact> items = [];
        for (int i = 0; i < ids.Count; i++)
        {
            string requestId = (i + 1).ToString();
            if (!responseById.TryGetValue(requestId, out var response))
            {
                throw new InvalidOperationException($"Microsoft Graph batch response was missing contact '{ids[i]}'.");
            }

            int status = response.GetProperty("status").GetInt32();
            if (status < 200 || status >= 300)
            {
                string detail = response.TryGetProperty("body", out var errorBody)
                    ? errorBody.GetRawText()
                    : "No response body was returned.";
                throw new InvalidOperationException($"Microsoft Graph batch contact request failed for '{ids[i]}' with {status}: {detail}");
            }

            if (!response.TryGetProperty("body", out var body))
            {
                throw new InvalidOperationException($"Microsoft Graph batch response for '{ids[i]}' did not include a contact payload.");
            }

            var contact = ConvertContact(body);
            if (contact is null)
            {
                continue;
            }

            ApplyExtendedPhoneProperties(contact, body);
            ContactPhotoLoader.Attach(contact, ct => PopulatePhotoAsync(contact, contact.Provenance.ProviderId, ct));
            items.Add(contact);
        }

        return items;
    }

    /// <summary>
    /// Builds the Microsoft Graph batch payload without <see cref="System.Text.Json.Nodes"/> so the connector stays friendly to AOT compilation.
    /// </summary>
    private string CreateBatchRequestBody(IReadOnlyList<string> ids)
    {
        StringBuilder body = new();
        body.Append("{\"requests\":[");
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0)
            {
                body.Append(',');
            }

            body.Append("{\"id\":\"")
                .Append(i + 1)
                .Append("\",\"method\":\"GET\",\"url\":")
                .Append('"')
                .Append(EscapeJsonString(CreateBatchRelativeContactUrl(ids[i])))
                .Append('"')
                .Append('}');
        }

        body.Append("]}");
        return body.ToString();
    }

    private static string EscapeJsonString(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private string CreateBatchRelativeContactUrl(string id)
    {
        string relativePath = new Uri(collectionPath).AbsolutePath;
        if (relativePath.StartsWith("/v1.0", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relativePath["/v1.0".Length..];
        }

        return $"{relativePath}/{Uri.EscapeDataString(id)}?$select={Uri.EscapeDataString(ContactSelectFields)}&{ExtendedPropertiesExpand}";
    }

    private sealed record CursorItemsPage(IReadOnlyList<CanonicalContact> Items, string? Cursor, bool HasMore);

    private sealed record AllItemsPage(IReadOnlyList<CanonicalContact> Items, string? NextLink);

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string requestUri, CancellationToken cancellationToken)
    {
        var token = await credential.TokenCredential.GetTokenAsync(new TokenRequestContext([MicrosoftScope]), cancellationToken);
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

    private static StringContent CreateJsonContent(string body) =>
        new(body, Encoding.UTF8, "application/json");

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
            // Always include the full extended-property set so absent values explicitly clear the Outlook property.
            ["singleValueExtendedProperties"] = BuildExtendedPhoneProperties(contact),
        };
    }

    private static JsonArray BuildExtendedPhoneProperties(CanonicalContact contact)
    {
        JsonArray array = [];
        foreach (var (label, graphId) in ExtendedPhoneProperties)
        {
            string? value = contact.Phones
                .FirstOrDefault(p => string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase))
                ?.Number;
            array.Add((JsonNode?)new JsonObject
            {
                ["id"] = graphId,
                ["value"] = value,
            });
        }

        return array;
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

    private static CanonicalContact? ConvertContact(JsonElement element)
    {
        string? id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (element.TryGetProperty("@removed", out _))
        {
            return new CanonicalContact
            {
                IsDeleted = true,
                Provenance =
                {
                    ProviderId = id,
                }
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
            Categories = ReadStringArray(element, "categories"),

            Provenance =
            {
                ProviderId = id,
                Version = ReadString(element, "@odata.etag"),
                LastModified = ReadDateTimeOffset(element, "lastModifiedDateTime"),
            }
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

        AddPhones(contact.Phones, element, "businessPhones", "work");
        AddPhones(contact.Phones, element, "homePhones", "home");
        string? mobilePhone = ReadString(element, "mobilePhone");
        if (!string.IsNullOrWhiteSpace(mobilePhone))
        {
            contact.Phones.Add(new ContactPhone { Number = mobilePhone, Label = "mobile" });
        }

        AddAddress(contact.Addresses, element, "homeAddress", "home");
        AddAddress(contact.Addresses, element, "businessAddress", "work");
        AddAddress(contact.Addresses, element, "otherAddress", "other");

        return ContentHashHelper.WithComputedHash(contact);
    }

    private async Task PopulatePhotoAsync(CanonicalContact contact, string id, CancellationToken cancellationToken)
    {
        var photo = await DownloadPhotoAsync(id, cancellationToken);
        if (photo is null)
        {
            ContactPhotoMetadataHelper.SetNoPhoto(contact);
            return;
        }

        ContactPhotoMetadataHelper.SetPhoto(contact, photo.Value.photoBytes, photo.Value.contentType);
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

    private async Task SyncPhotoAsync(string id, CanonicalContact contact, bool deleteWhenAbsent, CancellationToken cancellationToken)
    {
        if (ContactPhotoMetadataHelper.TryGetPhoto(contact, out byte[] photoBytes, out string contentType))
        {
            using var request = await CreateRequestAsync(HttpMethod.Put, $"{collectionPath}/{Uri.EscapeDataString(id)}/photo/$value", cancellationToken);
            request.Content = new ByteArrayContent(photoBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return;
        }

        if (!deleteWhenAbsent || !ContactPhotoMetadataHelper.HasKnownAbsence(contact))
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

    private static void AddPhones(List<ContactPhone> phones, JsonElement element, string propertyName, string label)
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

    private static void ApplyExtendedPhoneProperties(CanonicalContact contact, JsonElement element)
    {
        if (!element.TryGetProperty("singleValueExtendedProperties", out var extProps)
            || extProps.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // Build a lookup of extended property values by their Graph id.
        Dictionary<string, string?> valueById = [];
        foreach (var prop in extProps.EnumerateArray())
        {
            string? propId = ReadString(prop, "id");
            if (!string.IsNullOrWhiteSpace(propId))
            {
                valueById[propId] = ReadString(prop, "value");
            }
        }

        foreach (var (label, graphId) in ExtendedPhoneProperties)
        {
            if (!valueById.TryGetValue(graphId, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            contact.Phones.Add(new ContactPhone { Number = value, Label = label });
        }
    }

    private static void AddAddress(List<ContactAddress> addresses, JsonElement element, string propertyName, string label)
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
    private static string GetCollectionPath(string endpointName, EndpointOptions endpoint)
    {
        string userId = endpoint.Properties.GetRequiredValue("userId", $"endpoint '{endpointName}'");
        string? folderId = endpoint.Properties.GetOptionalValue("folderId");
        string path = folderId is null
            ? $"{GraphBaseUri}/users/{Uri.EscapeDataString(userId)}/contacts"
            : $"{GraphBaseUri}/users/{Uri.EscapeDataString(userId)}/contactFolders/{Uri.EscapeDataString(folderId)}/contacts";

        return path;
    }
}
