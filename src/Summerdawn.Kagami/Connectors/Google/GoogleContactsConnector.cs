namespace Summerdawn.Kagami.Connectors.Google;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

internal sealed class GoogleContactsConnector : IConnector
{
    private const string ContactsScope = "https://www.googleapis.com/auth/contacts";
    private const string PersonFields = "metadata,names,emailAddresses,phoneNumbers,addresses,organizations,biographies,birthdays,memberships,photos";
    private static readonly DateOnly DefaultBirthday = new(1900, 1, 1);

    private readonly HttpClient httpClient;
    private readonly ILogger<GoogleContactsConnector> logger;
    private readonly GoogleOAuthCredential credential;
    private Dictionary<string, string>? groupNamesByResource;
    private Dictionary<string, string>? groupResourcesByName;

    public GoogleContactsConnector(
        HttpClient httpClient,
        string endpointName,
        EndpointOptions endpoint,
        CredentialOptions credential,
        ILogger<GoogleContactsConnector> logger)
    {
        _ = endpointName;
        this.httpClient = httpClient;
        this.logger = logger;
        if (!string.IsNullOrWhiteSpace(credential.Type)
            && !string.Equals(credential.Type, "google-oauth", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Google contacts endpoint '{endpointName}' requires credential type 'google-oauth'.");
        }

        this.credential = CreateCredential(credential.Properties, httpClient);
    }

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "google-contacts",
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = false,
        SupportsRecurrence = false,
        SupportsContactPhotos = true,
        SupportsServerSideFiltering = false,
    };

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetAccessTokenAsync(cancellationToken);
    }

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        GetConnectionsPageAsync(new GoogleCursor(null, null, true), cancellationToken);

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
        GetConnectionsPageAsync(ParseCursor(cursor), cancellationToken);

    public async Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://people.googleapis.com/v1/{Uri.EscapeDataString(id)}?personFields={Uri.EscapeDataString(PersonFields)}";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using JsonDocument document = await SendForJsonAsync(request, cancellationToken);
        CanonicalItem? item = ConvertPerson(document.RootElement);
        if (item is not null && !item.IsDeleted)
        {
            await PopulatePhotoAsync(item, document.RootElement, cancellationToken);
        }

        return item;
    }

    public async Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        CanonicalContact contact = GetContactPayload(item);
        JsonElement body = await BuildWritablePersonAsync(contact, cancellationToken);
        string requestUri = $"https://people.googleapis.com/v1/people:createContact?personFields={Uri.EscapeDataString(PersonFields)}";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Post, requestUri, cancellationToken);
        request.Content = CreateJsonContent(body);
        using JsonDocument document = await SendForJsonAsync(request, cancellationToken);
        CanonicalItem created = ConvertPerson(document.RootElement) ?? throw new InvalidOperationException("Google createContact returned no person payload.");
        await SyncPhotoAsync(created.SourceId, item, deleteWhenAbsent: false, cancellationToken);
        return await GetItemAsync(created.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Google createContact succeeded but the created item could not be reloaded.");
    }

    public async Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        CanonicalContact contact = GetContactPayload(item);
        JsonObjectBuilder personBuilder = await BuildWritablePersonBuilderAsync(contact, cancellationToken);
        personBuilder.Add("resourceName", item.SourceId);
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            personBuilder.Add("etag", item.Version);
        }

        string updateFields = Uri.EscapeDataString("names,emailAddresses,phoneNumbers,addresses,organizations,biographies,birthdays,memberships");
        string requestUri = $"https://people.googleapis.com/v1/{Uri.EscapeDataString(item.SourceId)}:updateContact?updatePersonFields={updateFields}&personFields={Uri.EscapeDataString(PersonFields)}";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
        request.Content = CreateJsonContent(personBuilder.Build());
        using JsonDocument _ = await SendForJsonAsync(request, cancellationToken);
        await SyncPhotoAsync(item.SourceId, item, deleteWhenAbsent: true, cancellationToken);
        return await GetItemAsync(item.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Google updateContact succeeded but the updated item could not be reloaded.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://people.googleapis.com/v1/{Uri.EscapeDataString(id)}:deleteContact";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<IncrementalPage> GetConnectionsPageAsync(GoogleCursor cursor, CancellationToken cancellationToken)
    {
        StringBuilder requestUri = new("https://people.googleapis.com/v1/people/me/connections");
        requestUri.Append("?personFields=").Append(Uri.EscapeDataString(PersonFields));
        requestUri.Append("&sources=READ_SOURCE_TYPE_CONTACT");
        requestUri.Append("&pageSize=1000");
        if (cursor.RequestSyncToken)
        {
            requestUri.Append("&requestSyncToken=true");
        }

        if (!string.IsNullOrWhiteSpace(cursor.PageToken))
        {
            requestUri.Append("&pageToken=").Append(Uri.EscapeDataString(cursor.PageToken));
        }

        if (!string.IsNullOrWhiteSpace(cursor.SyncToken))
        {
            requestUri.Append("&syncToken=").Append(Uri.EscapeDataString(cursor.SyncToken));
        }

        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, requestUri.ToString(), cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone && cursor.SyncToken is not null)
        {
            logger.LogWarning("Google People sync token expired; falling back to a new full sync.");
            return await GetInitialPageAsync(cancellationToken);
        }

        await EnsureSuccessAsync(response, cancellationToken);
        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        List<CanonicalItem> items = [];
        if (document.RootElement.TryGetProperty("connections", out JsonElement connections))
        {
            foreach (JsonElement person in connections.EnumerateArray())
            {
                CanonicalItem? item = ConvertPerson(person);
                if (item is not null)
                {
                    if (!item.IsDeleted)
                    {
                        await PopulatePhotoAsync(item, person, cancellationToken);
                    }

                    items.Add(item);
                }
            }
        }

        string? nextPageToken = document.RootElement.TryGetProperty("nextPageToken", out JsonElement nextPageTokenElement)
            ? nextPageTokenElement.GetString()
            : null;
        string? nextSyncToken = document.RootElement.TryGetProperty("nextSyncToken", out JsonElement nextSyncTokenElement)
            ? nextSyncTokenElement.GetString()
            : cursor.SyncToken;

        return new IncrementalPage
        {
            Items = items,
            HasMore = nextPageToken is not null,
            NextCursor = nextPageToken is not null
                ? SerializeCursor(new GoogleCursor(cursor.SyncToken, nextPageToken, cursor.RequestSyncToken))
                : nextSyncToken is not null ? SerializeCursor(new GoogleCursor(nextSyncToken, null, false)) : null,
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
        HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Google People API request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private async Task<JsonElement> BuildWritablePersonAsync(CanonicalContact contact, CancellationToken cancellationToken)
    {
        JsonObjectBuilder builder = await BuildWritablePersonBuilderAsync(contact, cancellationToken);
        return builder.Build();
    }

    private async Task<JsonObjectBuilder> BuildWritablePersonBuilderAsync(CanonicalContact contact, CancellationToken cancellationToken)
    {
        JsonObjectBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(contact.GivenName) || !string.IsNullOrWhiteSpace(contact.FamilyName) || !string.IsNullOrWhiteSpace(contact.DisplayName))
        {
            builder.Add("names", new[]
            {
                new Dictionary<string, object?>
                {
                    ["givenName"] = contact.GivenName,
                    ["middleName"] = contact.MiddleName,
                    ["familyName"] = contact.FamilyName,
                    ["displayName"] = contact.DisplayName,
                },
            });
        }

        if (contact.Emails.Count > 0)
        {
            builder.Add("emailAddresses", contact.Emails.Select(email => new Dictionary<string, object?>
            {
                ["value"] = email.Address,
                ["type"] = email.Label,
            }).ToArray());
        }

        if (contact.Phones.Count > 0)
        {
            builder.Add("phoneNumbers", contact.Phones.Select(phone => new Dictionary<string, object?>
            {
                ["value"] = phone.Number,
                ["type"] = phone.Label,
            }).ToArray());
        }

        if (contact.Addresses.Count > 0)
        {
            builder.Add("addresses", contact.Addresses.Select(address => new Dictionary<string, object?>
            {
                ["streetAddress"] = address.Street,
                ["city"] = address.City,
                ["region"] = address.State,
                ["postalCode"] = address.PostalCode,
                ["country"] = address.Country,
                ["type"] = address.Label,
            }).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(contact.Organization) || !string.IsNullOrWhiteSpace(contact.Title))
        {
            builder.Add("organizations", new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = contact.Organization,
                    ["title"] = contact.Title,
                },
            });
        }

        if (!string.IsNullOrWhiteSpace(contact.Notes))
        {
            builder.Add("biographies", new[]
            {
                new Dictionary<string, object?>
                {
                    ["value"] = contact.Notes,
                    ["contentType"] = "TEXT_PLAIN",
                },
            });
        }

        if (contact.Birthday is not null)
        {
            builder.Add("birthdays", new[]
            {
                new Dictionary<string, object?>
                {
                    ["date"] = new Dictionary<string, object?>
                    {
                        ["year"] = contact.Birthday.Value.Year,
                        ["month"] = contact.Birthday.Value.Month,
                        ["day"] = contact.Birthday.Value.Day,
                    },
                },
            });
        }

        if (contact.Categories.Count > 0)
        {
            string[] resourceNames = await EnsureGroupsAsync(contact.Categories, cancellationToken);
            builder.Add("memberships", resourceNames.Select(resourceName => new Dictionary<string, object?>
            {
                ["contactGroupMembership"] = new Dictionary<string, object?>
                {
                    ["contactGroupResourceName"] = resourceName,
                },
            }).ToArray());
        }
        else
        {
            builder.Add("memberships", new[]
            {
                new Dictionary<string, object?>
                {
                    ["contactGroupMembership"] = new Dictionary<string, object?>
                    {
                        ["contactGroupResourceName"] = "contactGroups/myContacts",
                    },
                },
            });
        }

        return builder;
    }

    private static CanonicalContact GetContactPayload(CanonicalItem item) =>
        item.Payload as CanonicalContact
        ?? throw new InvalidOperationException("Google contacts connector only supports CanonicalContact payloads.");

    private CanonicalItem? ConvertPerson(JsonElement person)
    {
        string? resourceName = person.TryGetProperty("resourceName", out JsonElement resourceNameElement)
            ? resourceNameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        bool isDeleted = person.TryGetProperty("metadata", out JsonElement metadata)
            && metadata.TryGetProperty("deleted", out JsonElement deletedElement)
            && deletedElement.ValueKind == JsonValueKind.True;
        if (isDeleted)
        {
            return new CanonicalItem
            {
                EntityType = EntityType.Contact,
                SourceId = resourceName,
                IsDeleted = true,
            };
        }

        CanonicalContact contact = new()
        {
            GivenName = ReadFirstNestedString(person, "names", "givenName"),
            MiddleName = ReadFirstNestedString(person, "names", "middleName"),
            FamilyName = ReadFirstNestedString(person, "names", "familyName"),
            DisplayName = ReadFirstNestedString(person, "names", "displayName") ?? string.Empty,
            Organization = ReadFirstNestedString(person, "organizations", "name"),
            Title = ReadFirstNestedString(person, "organizations", "title"),
            Notes = ReadFirstNestedString(person, "biographies", "value"),
            Birthday = ReadBirthday(person),
            LastModified = ReadLastModified(person),
            Categories = ReadMemberships(person),
        };

        if (person.TryGetProperty("emailAddresses", out JsonElement emails))
        {
            foreach (JsonElement email in emails.EnumerateArray())
            {
                string? value = email.TryGetProperty("value", out JsonElement valueElement) ? valueElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    contact.Emails.Add(new ContactEmail
                    {
                        Address = value,
                        Label = email.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null,
                    });
                }
            }
        }

        if (person.TryGetProperty("phoneNumbers", out JsonElement phones))
        {
            foreach (JsonElement phone in phones.EnumerateArray())
            {
                string? value = phone.TryGetProperty("value", out JsonElement valueElement) ? valueElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    contact.Phones.Add(new ContactPhone
                    {
                        Number = value,
                        Label = phone.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null,
                    });
                }
            }
        }

        if (person.TryGetProperty("addresses", out JsonElement addresses))
        {
            foreach (JsonElement address in addresses.EnumerateArray())
            {
                contact.Addresses.Add(new ContactAddress
                {
                    Label = address.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null,
                    Street = address.TryGetProperty("streetAddress", out JsonElement streetElement) ? streetElement.GetString() : null,
                    City = address.TryGetProperty("city", out JsonElement cityElement) ? cityElement.GetString() : null,
                    State = address.TryGetProperty("region", out JsonElement stateElement) ? stateElement.GetString() : null,
                    PostalCode = address.TryGetProperty("postalCode", out JsonElement postalElement) ? postalElement.GetString() : null,
                    Country = address.TryGetProperty("country", out JsonElement countryElement) ? countryElement.GetString() : null,
                });
            }
        }

        return CanonicalItemSerializer.WithComputedHash(new CanonicalItem
        {
            EntityType = EntityType.Contact,
            Payload = contact,
            SourceId = resourceName,
            Version = person.TryGetProperty("etag", out JsonElement etagElement) ? etagElement.GetString() : null,
            Metadata = new Dictionary<string, string>(),
        });
    }

    private async Task PopulatePhotoAsync(CanonicalItem item, JsonElement person, CancellationToken cancellationToken)
    {
        string? photoUrl = ReadPhotoUrl(person);
        if (string.IsNullOrWhiteSpace(photoUrl))
        {
            ContactPhotoMetadata.SetNoPhoto(item);
            return;
        }

        (byte[] photoBytes, string contentType)? photo = await DownloadPhotoAsync(photoUrl, cancellationToken);
        if (photo is null)
        {
            ContactPhotoMetadata.SetNoPhoto(item);
            return;
        }

        ContactPhotoMetadata.SetPhoto(item, photo.Value.photoBytes, photo.Value.contentType);
    }

    private async Task<(byte[] photoBytes, string contentType)?> DownloadPhotoAsync(string photoUrl, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, photoUrl, cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
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

    private async Task SyncPhotoAsync(string resourceName, CanonicalItem item, bool deleteWhenAbsent, CancellationToken cancellationToken)
    {
        if (ContactPhotoMetadata.TryGetPhoto(item, out byte[] photoBytes, out _))
        {
            JsonElement body = JsonSerializer.SerializeToElement(new
            {
                photoBytes = Convert.ToBase64String(photoBytes),
            });
            string requestUri = $"https://people.googleapis.com/v1/{Uri.EscapeDataString(resourceName)}:updateContactPhoto";
            using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
            request.Content = CreateJsonContent(body);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return;
        }

        if (!deleteWhenAbsent || !ContactPhotoMetadata.HasKnownAbsence(item))
        {
            return;
        }

        string deleteUri = $"https://people.googleapis.com/v1/{Uri.EscapeDataString(resourceName)}:deleteContactPhoto";
        using HttpRequestMessage deleteRequest = await CreateRequestAsync(HttpMethod.Delete, deleteUri, cancellationToken);
        using HttpResponseMessage deleteResponse = await httpClient.SendAsync(deleteRequest, cancellationToken);
        if (deleteResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(deleteResponse, cancellationToken);
    }

    private async Task<string[]> EnsureGroupsAsync(IReadOnlyList<string> categories, CancellationToken cancellationToken)
    {
        await EnsureGroupCacheAsync(cancellationToken);
        List<string> resourceNames = [];
        foreach (string category in categories.Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (groupResourcesByName!.TryGetValue(category, out string? resourceName))
            {
                resourceNames.Add(resourceName);
                continue;
            }

            string createdResourceName = await CreateGroupAsync(category, cancellationToken);
            groupResourcesByName[category] = createdResourceName;
            groupNamesByResource![createdResourceName] = category;
            resourceNames.Add(createdResourceName);
        }

        if (!resourceNames.Contains("contactGroups/myContacts", StringComparer.Ordinal))
        {
            resourceNames.Insert(0, "contactGroups/myContacts");
        }

        return [.. resourceNames];
    }

    private async Task EnsureGroupCacheAsync(CancellationToken cancellationToken)
    {
        if (groupNamesByResource is not null && groupResourcesByName is not null)
        {
            return;
        }

        groupNamesByResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        groupResourcesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string requestUri = "https://people.googleapis.com/v1/contactGroups?pageSize=1000&groupFields=name,groupType";
        string? pageToken = null;
        do
        {
            string currentRequestUri = pageToken is null
                ? requestUri
                : $"{requestUri}&pageToken={Uri.EscapeDataString(pageToken)}";
            using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, currentRequestUri, cancellationToken);
            using JsonDocument document = await SendForJsonAsync(request, cancellationToken);
            if (document.RootElement.TryGetProperty("contactGroups", out JsonElement groups))
            {
                foreach (JsonElement group in groups.EnumerateArray())
                {
                    string? resourceName = group.TryGetProperty("resourceName", out JsonElement resourceNameElement)
                        ? resourceNameElement.GetString()
                        : null;
                    string? name = group.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(resourceName) && !string.IsNullOrWhiteSpace(name))
                    {
                        groupNamesByResource[resourceName] = name;
                        groupResourcesByName[name] = resourceName;
                    }
                }
            }

            pageToken = document.RootElement.TryGetProperty("nextPageToken", out JsonElement nextPageToken)
                ? nextPageToken.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
    }

    private async Task<string> CreateGroupAsync(string name, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Post, "https://people.googleapis.com/v1/contactGroups", cancellationToken);
        request.Content = CreateJsonContent(JsonSerializer.SerializeToElement(new
        {
            contactGroup = new
            {
                name,
            },
        }));

        using JsonDocument document = await SendForJsonAsync(request, cancellationToken);
        return document.RootElement.GetProperty("resourceName").GetString()
            ?? throw new InvalidOperationException($"Google contact group '{name}' creation did not return a resource name.");
    }

    private List<string> ReadMemberships(JsonElement person)
    {
        List<string> categories = [];
        if (!person.TryGetProperty("memberships", out JsonElement memberships))
        {
            return categories;
        }

        if (groupNamesByResource is null)
        {
            groupNamesByResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["contactGroups/myContacts"] = "My Contacts",
                ["contactGroups/starred"] = "Starred",
            };
        }

        foreach (JsonElement membership in memberships.EnumerateArray())
        {
            if (!membership.TryGetProperty("contactGroupMembership", out JsonElement contactGroupMembership))
            {
                continue;
            }

            string? resourceName = contactGroupMembership.TryGetProperty("contactGroupResourceName", out JsonElement resourceNameElement)
                ? resourceNameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                continue;
            }

            categories.Add(groupNamesByResource.TryGetValue(resourceName, out string? name) ? name : resourceName);
        }

        return categories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DateOnly? ReadBirthday(JsonElement person)
    {
        if (!person.TryGetProperty("birthdays", out JsonElement birthdays) || birthdays.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement date = birthdays[0].GetProperty("date");
        int year = date.TryGetProperty("year", out JsonElement yearElement) && yearElement.TryGetInt32(out int parsedYear)
            ? parsedYear
            : DefaultBirthday.Year;
        int month = date.TryGetProperty("month", out JsonElement monthElement) && monthElement.TryGetInt32(out int parsedMonth)
            ? parsedMonth
            : DefaultBirthday.Month;
        int day = date.TryGetProperty("day", out JsonElement dayElement) && dayElement.TryGetInt32(out int parsedDay)
            ? parsedDay
            : DefaultBirthday.Day;
        return new DateOnly(year, month, day);
    }

    private static DateTimeOffset? ReadLastModified(JsonElement person)
    {
        if (!person.TryGetProperty("metadata", out JsonElement metadata)
            || !metadata.TryGetProperty("sources", out JsonElement sources))
        {
            return null;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (source.TryGetProperty("updateTime", out JsonElement updateTimeElement)
                && DateTimeOffset.TryParse(updateTimeElement.GetString(), out DateTimeOffset updateTime))
            {
                return updateTime;
            }
        }

        return null;
    }

    private static string? ReadPhotoUrl(JsonElement person)
    {
        if (!person.TryGetProperty("photos", out JsonElement photos) || photos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement photo in photos.EnumerateArray())
        {
            string? url = photo.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            bool isDefault = photo.TryGetProperty("default", out JsonElement defaultElement) && defaultElement.ValueKind == JsonValueKind.True;
            if (!isDefault)
            {
                return url;
            }
        }

        return null;
    }

    private static string? ReadFirstNestedString(JsonElement element, string arrayPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(arrayPropertyName, out JsonElement array) || array.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = array[0];
        return first.TryGetProperty(propertyName, out JsonElement propertyElement) ? propertyElement.GetString() : null;
    }

    private static StringContent CreateJsonContent(JsonElement body) =>
        new(body.GetRawText(), Encoding.UTF8, "application/json");

    private static GoogleOAuthCredential CreateCredential(IReadOnlyDictionary<string, string> properties, HttpClient httpClient)
    {
        string clientId = properties.GetRequiredValue("clientId", "google-oauth credential");
        string clientSecret = properties.GetRequiredValue("clientSecret", "google-oauth credential");
        string userLogin = properties.GetRequiredValue("userLogin", "google-oauth credential");
        return new GoogleOAuthCredential(clientId, clientSecret, userLogin, [ContactsScope], httpClient);
    }

    private static GoogleCursor ParseCursor(string cursor) =>
        JsonSerializer.Deserialize<GoogleCursor>(cursor)
        ?? throw new InvalidOperationException("Invalid Google connector cursor.");

    private static string SerializeCursor(GoogleCursor cursor) =>
        JsonSerializer.Serialize(cursor);

    private sealed record GoogleCursor(string? SyncToken, string? PageToken, bool RequestSyncToken);

    private sealed class JsonObjectBuilder
    {
        private readonly Dictionary<string, object?> values = [];

        public void Add(string name, object? value) => values[name] = value;

        public JsonElement Build() => JsonSerializer.SerializeToElement(values);
    }
}
