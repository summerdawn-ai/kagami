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
/// Synchronizes Google People contacts by combining connection-list enumeration with person hydration.
/// </summary>
#pragma warning disable CS9113 // Parameter is unread.
public sealed class GoogleContactsConnector(HttpClient httpClient, string endpointName, EndpointOptions endpoint, GoogleOAuthCredential credential, ILogger<GoogleContactsConnector> logger) : IConnector<CanonicalContact>
#pragma warning restore CS9113 // Parameter is unread.
{
    private const string PersonFields = "metadata,names,emailAddresses,phoneNumbers,addresses,organizations,biographies,birthdays,memberships,photos";
    // Load larger metadata-only pages up front so exports and list operations spend less time round-tripping for page tokens.
    private const int ConnectionsPageSize = 200;
    // Google batchGet limits matching to 50 ids.
    private const int MaxBatchSize = 50;
    private static readonly DateOnly DefaultBirthday = new(1604, 1, 1);

    private static readonly HashSet<string> SystemGroupResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "contactGroups/myContacts",
        "contactGroups/starred",
    };

    private Dictionary<string, string>? groupNamesByResource;
    private Dictionary<string, string>? groupResourcesByName;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = EndpointOptions.GoogleContacts,
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = false,
        SupportsRecurrence = false,
        SupportsContactPhotos = true,
        SupportsServerSideFiltering = false,
    };

    public string EndpointName { get; } = endpointName;

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetAccessTokenAsync(cancellationToken);
    }

    public async Task<ItemSet<CanonicalContact>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        var pageCursor = cursor is null
            ? new GoogleCursor(null, null, true)
            : ParseCursor(cursor);

        List<CanonicalContact> items = [];
        string? finalCursor = cursor;
        while (true)
        {
            var page = await GetConnectionsPageAsync(pageCursor, cancellationToken);
            items.AddRange(page.Items);
            finalCursor = page.Cursor;
            if (!page.HasMore)
            {
                break;
            }

            if (page.Cursor is null)
            {
                throw new InvalidOperationException("Google connector returned HasMore=true without a cursor.");
            }

            pageCursor = ParseCursor(page.Cursor);
        }

        return new ItemSet<CanonicalContact>(items, finalCursor);
    }

    public async Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureGroupsReadAsync(cancellationToken);
        string requestUri = $"https://people.googleapis.com/v1/{id}?personFields={Uri.EscapeDataString(PersonFields)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var item = ConvertPerson(document.RootElement, groupNamesByResource!, EndpointName);
        if (item is not null && !item.IsDeleted)
        {
            await PopulatePhotoAsync(item, document.RootElement, cancellationToken);
        }

        return item;
    }

    public async Task<CanonicalContact> CreateItemAsync(CanonicalContact contact, CancellationToken cancellationToken = default)
    {
        await EnsureGroupsWrittenAsync(contact.Categories, cancellationToken);
        var body = BuildWritablePerson(contact, groupNamesByResource!);
        string requestUri = $"https://people.googleapis.com/v1/people:createContact?personFields={Uri.EscapeDataString(PersonFields)}";
        using var request = await CreateRequestAsync(HttpMethod.Post, requestUri, cancellationToken);
        request.Content = CreateJsonContent(body);
        using var document = await SendForJsonAsync(request, cancellationToken);
        var created = ConvertPerson(document.RootElement, groupNamesByResource!, EndpointName) ?? throw new InvalidOperationException("Google createContact returned no person payload.");
        await SyncPhotoAsync(created.Provenance.ProviderId, contact, deleteWhenAbsent: false, cancellationToken);
        return await GetItemAsync(created.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Google createContact succeeded but the created item could not be reloaded.");
    }

    public async Task<CanonicalContact> UpdateItemAsync(CanonicalContact contact, CancellationToken cancellationToken = default)
    {
        await EnsureGroupsWrittenAsync(contact.Categories, cancellationToken);
        var person = BuildWritablePerson(contact, groupNamesByResource!);
        person["resourceName"] = contact.Provenance.ProviderId;
        if (!string.IsNullOrWhiteSpace(contact.Provenance.Version))
        {
            person["etag"] = contact.Provenance.Version;
        }

        string updateFields = Uri.EscapeDataString("names,emailAddresses,phoneNumbers,addresses,organizations,biographies,birthdays,memberships");
        string requestUri = $"https://people.googleapis.com/v1/{contact.Provenance.ProviderId}:updateContact?updatePersonFields={updateFields}&personFields={Uri.EscapeDataString(PersonFields)}";
        using var request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
        request.Content = CreateJsonContent(person);
        using var _ = await SendForJsonAsync(request, cancellationToken);
        await SyncPhotoAsync(contact.Provenance.ProviderId, contact, deleteWhenAbsent: true, cancellationToken);
        return await GetItemAsync(contact.Provenance.ProviderId, cancellationToken)
            ?? throw new InvalidOperationException("Google updateContact succeeded but the updated item could not be reloaded.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string requestUri = $"https://people.googleapis.com/v1/{id}:deleteContact";
        using var request = await CreateRequestAsync(HttpMethod.Delete, requestUri, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<ConnectorPage> GetConnectionsPageAsync(GoogleCursor cursor, CancellationToken cancellationToken)
    {
        await EnsureGroupsReadAsync(cancellationToken);
        StringBuilder requestUri = new("https://people.googleapis.com/v1/people/me/connections");
        requestUri.Append("?personFields=").Append(Uri.EscapeDataString("metadata"));
        requestUri.Append("&sources=READ_SOURCE_TYPE_CONTACT");
        requestUri.Append("&pageSize=").Append(ConnectionsPageSize);
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

        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri.ToString(), cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone && cursor.SyncToken is not null)
        {
            logger.LogWarning("Google People sync token expired; falling back to a new full sync.");
            return await GetConnectionsPageAsync(new GoogleCursor(null, null, true), cancellationToken);
        }

        await EnsureSuccessAsync(response, cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        List<CanonicalContact> items = [];
        List<string> nonDeletedResourceNames = [];
        if (document.RootElement.TryGetProperty("connections", out var connections))
        {
            foreach (var connection in connections.EnumerateArray())
            {
                string? resourceName = connection.TryGetProperty("resourceName", out var resourceNameElement)
                    ? resourceNameElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(resourceName))
                {
                    continue;
                }

                bool isDeleted = connection.TryGetProperty("metadata", out var metadata)
                    && metadata.TryGetProperty("deleted", out var deletedElement)
                    && deletedElement.ValueKind == JsonValueKind.True;

                if (isDeleted)
                {
                    items.Add(new CanonicalContact
                    {
                        Provenance =
                        {
                            ProviderId = resourceName
                        },
                        IsDeleted = true,
                    });
                }
                else
                {
                    nonDeletedResourceNames.Add(resourceName);
                }
            }
        }

        if (nonDeletedResourceNames.Count > 0)
        {
            var batchItems = await BatchGetPeopleAsync(nonDeletedResourceNames, cancellationToken);
            items.AddRange(batchItems);
        }

        string? nextPageToken = document.RootElement.TryGetProperty("nextPageToken", out var nextPageTokenElement)
            ? nextPageTokenElement.GetString()
            : null;
        string? nextSyncToken = document.RootElement.TryGetProperty("nextSyncToken", out var nextSyncTokenElement)
            ? nextSyncTokenElement.GetString()
            : cursor.SyncToken;

        string? nextCursor = nextPageToken is not null
            ? SerializeCursor(new GoogleCursor(cursor.SyncToken, nextPageToken, cursor.RequestSyncToken))
            : nextSyncToken is not null ? SerializeCursor(new GoogleCursor(nextSyncToken, null, false)) : null;

        return new ConnectorPage(items, nextCursor, nextPageToken is not null);
    }

    /// <summary>
    /// Hydrates the metadata-only connection page by loading the full Google People payload for just the ids on that page.
    /// </summary>
    /// <remarks>
    /// Google connection entries expose the connection version rather than the underlying person version, so the connector must always hydrate
    /// each page through <c>people:batchGet</c> to read the authoritative person etag used for change tracking and export correctness.
    /// </remarks>
    /// <remarks>
    /// Google <c>people:batchGet</c> is still a GET with repeated <c>resourceNames</c> query parameters, so the page is split into
    /// smaller chunks to avoid building an oversized request URI when the connector loads 200 contacts at a time.
    /// </remarks>
    private async Task<List<CanonicalContact>> BatchGetPeopleAsync(List<string> resourceNames, CancellationToken cancellationToken)
    {
        List<CanonicalContact> items = [];
        foreach (string[] chunk in resourceNames.Chunk(MaxBatchSize))
        {
            items.AddRange(await BatchGetPeopleChunkAsync(chunk, cancellationToken));
        }

        return items;
    }

    /// <summary>
    /// Executes a single Google <c>people:batchGet</c> request for one resource-name chunk and attaches deferred photo loading to each hydrated contact.
    /// </summary>
    private async Task<List<CanonicalContact>> BatchGetPeopleChunkAsync(IReadOnlyList<string> resourceNames, CancellationToken cancellationToken)
    {
        StringBuilder requestUri = new("https://people.googleapis.com/v1/people:batchGet");
        requestUri.Append("?personFields=").Append(Uri.EscapeDataString(PersonFields));
        foreach (string resourceName in resourceNames)
        {
            requestUri.Append("&resourceNames=").Append(Uri.EscapeDataString(resourceName));
        }

        using var request = await CreateRequestAsync(HttpMethod.Get, requestUri.ToString(), cancellationToken);
        using var document = await SendForJsonAsync(request, cancellationToken);

        List<CanonicalContact> items = [];
        if (document.RootElement.TryGetProperty("responses", out var responses))
        {
            foreach (var responseElement in responses.EnumerateArray())
            {
                if (!responseElement.TryGetProperty("person", out var person))
                {
                    continue;
                }

                var item = ConvertPerson(person, groupNamesByResource!, EndpointName);
                if (item is not null)
                {
                    if (!item.IsDeleted)
                    {
                        var personClone = person.Clone();
                        ContactPhotoLoader.Attach(item, ct => PopulatePhotoAsync(item, personClone, ct));
                    }

                    items.Add(item);
                }
            }
        }

        return items;
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
        throw new InvalidOperationException($"Google People API request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    /// <summary>
    /// Builds a Google People API writable person payload from a canonical contact.
    /// </summary>
    /// <param name="contact">
    /// The canonical contact to serialize.
    /// </param>
    /// <param name="groupNamesByResource">
    /// A mapping of Google contact-group resource names to human-readable group names,
    /// used to resolve canonical category names back to resource names via reverse-lookup.
    /// </param>
    internal static JsonObject BuildWritablePerson(CanonicalContact contact, IReadOnlyDictionary<string, string> groupNamesByResource)
    {
        JsonObject person = [];
        if (!string.IsNullOrWhiteSpace(contact.GivenName) || !string.IsNullOrWhiteSpace(contact.FamilyName) || !string.IsNullOrWhiteSpace(contact.DisplayName))
        {
            person["names"] = CreateArray(
                new JsonObject
                {
                    ["givenName"] = contact.GivenName,
                    ["middleName"] = contact.MiddleName,
                    ["familyName"] = contact.FamilyName,
                    ["displayName"] = contact.DisplayName,
                });
        }

        if (contact.Emails.Count > 0)
        {
            person["emailAddresses"] = CreateArray(contact.Emails.Select(email => (JsonNode?)new JsonObject
            {
                ["value"] = email.Address,
                ["type"] = email.Label,
            }));
        }

        if (contact.Phones.Count > 0)
        {
            person["phoneNumbers"] = CreateArray(contact.Phones.Select(phone => (JsonNode?)new JsonObject
            {
                ["value"] = phone.Number,
                ["type"] = phone.Label,
            }));
        }

        if (contact.Addresses.Count > 0)
        {
            person["addresses"] = CreateArray(contact.Addresses.Select(address => (JsonNode?)new JsonObject
            {
                ["streetAddress"] = address.Street,
                ["city"] = address.City,
                ["region"] = address.State,
                ["postalCode"] = address.PostalCode,
                ["country"] = address.Country,
                ["type"] = address.Label,
            }));
        }

        if (!string.IsNullOrWhiteSpace(contact.Organization) || !string.IsNullOrWhiteSpace(contact.Title))
        {
            person["organizations"] = CreateArray(
                new JsonObject
                {
                    ["name"] = contact.Organization,
                    ["title"] = contact.Title,
                });
        }

        if (!string.IsNullOrWhiteSpace(contact.Notes))
        {
            person["biographies"] = CreateArray(
                new JsonObject
                {
                    ["value"] = contact.Notes,
                    ["contentType"] = "TEXT_PLAIN",
                });
        }

        if (contact.Birthday is not null)
        {
            JsonObject date = new()
            {
                ["month"] = contact.Birthday.Value.Month,
                ["day"] = contact.Birthday.Value.Day,
            };

            if (contact.Birthday.Value.Year != DefaultBirthday.Year)
            {
                date["year"] = contact.Birthday.Value.Year;
            }

            person["birthdays"] = CreateArray(
                new JsonObject
                {
                    ["date"] = date,
                });
        }

        // Always include myContacts so the contact remains in at least one group (required by Google).
        List<JsonNode?> membershipNodes =
        [
            new JsonObject
            {
                ["contactGroupMembership"] = new JsonObject
                {
                    ["contactGroupResourceName"] = "contactGroups/myContacts",
                },
            },
        ];

        foreach (string category in contact.Categories
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Reverse-lookup: find the resource name whose display name matches this category.
            string? resourceName = null;
            foreach (var kv in groupNamesByResource)
            {
                if (string.Equals(kv.Value, category, StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = kv.Key;
                    break;
                }
            }

            // Skip unknown categories and system groups — orchestration must have ensured groups exist.
            if (resourceName is null || SystemGroupResourceNames.Contains(resourceName))
            {
                continue;
            }

            membershipNodes.Add(new JsonObject
            {
                ["contactGroupMembership"] = new JsonObject
                {
                    ["contactGroupResourceName"] = resourceName,
                },
            });
        }

        person["memberships"] = CreateArray(membershipNodes);

        return person;
    }

    /// <summary>
    /// Converts a Google People API person element to a canonical contact.
    /// </summary>
    /// <param name="person">
    /// The JSON element representing the People API person payload.
    /// </param>
    /// <param name="groupNamesByResource">
    /// A mapping of Google contact-group resource names to human-readable group names.
    /// System groups (<c>contactGroups/myContacts</c>, <c>contactGroups/starred</c>) are filtered out;
    /// custom groups are translated using this mapping, falling back to the raw resource name if unknown.
    /// </param>
    /// <param name="endpointName">The name of the endpoint from which this contact was retrieved.</param>
    internal static CanonicalContact? ConvertPerson(JsonElement person, IReadOnlyDictionary<string, string> groupNamesByResource, string endpointName)
    {
        string? resourceName = person.TryGetProperty("resourceName", out var resourceNameElement)
            ? resourceNameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        bool isDeleted = person.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("deleted", out var deletedElement)
            && deletedElement.ValueKind == JsonValueKind.True;
        if (isDeleted)
        {
            return new CanonicalContact
            {
                IsDeleted = true,
                Provenance = {
                    ProviderId = resourceName,
                    EndpointName = endpointName
                }
            };
        }

        CanonicalContact contact = new()
        {
            GivenName = NullIfEmpty(ReadFirstNestedString(person, "names", "givenName")?.Trim()),
            MiddleName = NullIfEmpty(ReadFirstNestedString(person, "names", "middleName")?.Trim()),
            FamilyName = NullIfEmpty(ReadFirstNestedString(person, "names", "familyName")?.Trim()),
            DisplayName = NormalizeDisplayName(ReadFirstNestedString(person, "names", "displayName")),
            Organization = ReadFirstNestedString(person, "organizations", "name"),
            Title = ReadFirstNestedString(person, "organizations", "title"),
            Notes = ReadFirstNestedString(person, "biographies", "value"),
            Birthday = ReadBirthday(person),
            Categories = ReadMemberships(person, groupNamesByResource),

            Provenance =
            {
                ProviderId = resourceName,
                EndpointName = endpointName,
                Version = person.TryGetProperty("etag", out var etagElement) ? etagElement.GetString() : null,
                LastModified = ReadLastModified(person),
            }
        };

        if (person.TryGetProperty("emailAddresses", out var emails))
        {
            foreach (var email in emails.EnumerateArray())
            {
                string? value = email.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    contact.Emails.Add(new ContactEmail
                    {
                        Address = value,
                        Label = email.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null,
                    });
                }
            }
        }

        if (person.TryGetProperty("phoneNumbers", out var phones))
        {
            foreach (var phone in phones.EnumerateArray())
            {
                string? value = phone.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    contact.Phones.Add(new ContactPhone
                    {
                        Number = value,
                        Label = phone.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null,
                    });
                }
            }
        }

        if (person.TryGetProperty("addresses", out var addresses))
        {
            foreach (var address in addresses.EnumerateArray())
            {
                contact.Addresses.Add(new ContactAddress
                {
                    Label = address.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null,
                    Street = address.TryGetProperty("streetAddress", out var streetElement) ? streetElement.GetString() : null,
                    City = address.TryGetProperty("city", out var cityElement) ? cityElement.GetString() : null,
                    State = address.TryGetProperty("region", out var stateElement) ? stateElement.GetString() : null,
                    PostalCode = address.TryGetProperty("postalCode", out var postalElement) ? postalElement.GetString() : null,
                    Country = address.TryGetProperty("country", out var countryElement) ? countryElement.GetString() : null,
                });
            }
        }

        return ContentHashHelper.WithComputedHash(contact);
    }

    private async Task PopulatePhotoAsync(CanonicalContact item, JsonElement person, CancellationToken cancellationToken)
    {
        string? photoUrl = ReadPhotoUrl(person);
        if (string.IsNullOrWhiteSpace(photoUrl))
        {
            ContactPhotoMetadataHelper.SetNoPhoto(item);
            return;
        }

        var photo = await DownloadPhotoAsync(photoUrl, cancellationToken);
        if (photo is null)
        {
            ContactPhotoMetadataHelper.SetNoPhoto(item);
            return;
        }

        ContactPhotoMetadataHelper.SetPhoto(item, photo.Value.photoBytes, photo.Value.contentType);
    }

    private async Task<(byte[] photoBytes, string contentType)?> DownloadPhotoAsync(string photoUrl, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, photoUrl, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
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

    private async Task SyncPhotoAsync(string personId, CanonicalContact item, bool deleteWhenAbsent, CancellationToken cancellationToken)
    {
        if (ContactPhotoMetadataHelper.TryGetPhoto(item, out byte[] photoBytes, out _))
        {
            JsonObject body = new()
            {
                ["photoBytes"] = Convert.ToBase64String(photoBytes),
            };
            string requestUri = $"https://people.googleapis.com/v1/{personId}:updateContactPhoto";
            using var request = await CreateRequestAsync(HttpMethod.Patch, requestUri, cancellationToken);
            request.Content = CreateJsonContent(body);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            return;
        }

        if (!deleteWhenAbsent || !ContactPhotoMetadataHelper.HasKnownAbsence(item))
        {
            return;
        }

        string deleteUri = $"https://people.googleapis.com/v1/{personId}:deleteContactPhoto";
        using var deleteRequest = await CreateRequestAsync(HttpMethod.Delete, deleteUri, cancellationToken);
        using var deleteResponse = await httpClient.SendAsync(deleteRequest, cancellationToken);
        if (deleteResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(deleteResponse, cancellationToken);
    }

    private async Task EnsureGroupsWrittenAsync(IReadOnlyList<string> categories, CancellationToken cancellationToken)
    {
        await EnsureGroupsReadAsync(cancellationToken);
        foreach (string category in categories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (groupResourcesByName!.ContainsKey(category))
            {
                continue;
            }

            string createdResourceName = await CreateGroupAsync(category, cancellationToken);
            groupResourcesByName[category] = createdResourceName;
            groupNamesByResource![createdResourceName] = category;
        }
    }

    private async Task EnsureGroupsReadAsync(CancellationToken cancellationToken)
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
            using var request = await CreateRequestAsync(HttpMethod.Get, currentRequestUri, cancellationToken);
            using var document = await SendForJsonAsync(request, cancellationToken);
            if (document.RootElement.TryGetProperty("contactGroups", out var groups))
            {
                foreach (var group in groups.EnumerateArray())
                {
                    string? resourceName = group.TryGetProperty("resourceName", out var resourceNameElement)
                        ? resourceNameElement.GetString()
                        : null;
                    string? name = group.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(resourceName) && !string.IsNullOrWhiteSpace(name))
                    {
                        groupNamesByResource[resourceName] = name;
                        groupResourcesByName[name] = resourceName;
                    }
                }
            }

            pageToken = document.RootElement.TryGetProperty("nextPageToken", out var nextPageToken)
                ? nextPageToken.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
    }

    private async Task<string> CreateGroupAsync(string name, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "https://people.googleapis.com/v1/contactGroups", cancellationToken);
        request.Content = CreateJsonContent(new JsonObject
        {
            ["contactGroup"] = new JsonObject
            {
                ["name"] = name,
            },
        });

        using var document = await SendForJsonAsync(request, cancellationToken);
        return document.RootElement.GetProperty("resourceName").GetString()
            ?? throw new InvalidOperationException($"Google contact group '{name}' creation did not return a resource name.");
    }

    private static List<string> ReadMemberships(JsonElement person, IReadOnlyDictionary<string, string> groupNamesByResource)
    {
        List<string> categories = [];
        if (!person.TryGetProperty("memberships", out var memberships))
        {
            return categories;
        }

        foreach (var membership in memberships.EnumerateArray())
        {
            if (!membership.TryGetProperty("contactGroupMembership", out var contactGroupMembership))
            {
                continue;
            }

            string? resourceName = contactGroupMembership.TryGetProperty("contactGroupResourceName", out var resourceNameElement)
                ? resourceNameElement.GetString()
                : null;

            // Skip missing resource names and system groups (myContacts, starred).
            if (string.IsNullOrWhiteSpace(resourceName) || SystemGroupResourceNames.Contains(resourceName))
            {
                continue;
            }

            // Translate to display name; skip if not in the cache.
            if (groupNamesByResource.TryGetValue(resourceName, out string? displayName))
            {
                categories.Add(displayName);
            }
        }

        return categories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DateOnly? ReadBirthday(JsonElement person)
    {
        if (!person.TryGetProperty("birthdays", out var birthdays) || birthdays.GetArrayLength() == 0)
        {
            return null;
        }

        var date = birthdays[0].GetProperty("date");
        int year = date.TryGetProperty("year", out var yearElement) && yearElement.TryGetInt32(out int parsedYear)
            ? parsedYear
            : DefaultBirthday.Year;
        int month = date.TryGetProperty("month", out var monthElement) && monthElement.TryGetInt32(out int parsedMonth)
            ? parsedMonth
            : DefaultBirthday.Month;
        int day = date.TryGetProperty("day", out var dayElement) && dayElement.TryGetInt32(out int parsedDay)
            ? parsedDay
            : DefaultBirthday.Day;
        return new DateOnly(year, month, day);
    }

    private static DateTimeOffset? ReadLastModified(JsonElement person)
    {
        if (!person.TryGetProperty("metadata", out var metadata)
            || !metadata.TryGetProperty("sources", out var sources))
        {
            return null;
        }

        foreach (var source in sources.EnumerateArray())
        {
            if (source.TryGetProperty("updateTime", out var updateTimeElement)
                && DateTimeOffset.TryParse(updateTimeElement.GetString(), out var updateTime))
            {
                return updateTime;
            }
        }

        return null;
    }

    private static string? ReadPhotoUrl(JsonElement person)
    {
        if (!person.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var photo in photos.EnumerateArray())
        {
            string? url = photo.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            bool isDefault = photo.TryGetProperty("default", out var defaultElement) && defaultElement.ValueKind == JsonValueKind.True;
            if (!isDefault)
            {
                return url;
            }
        }

        return null;
    }

    private static string? ReadFirstNestedString(JsonElement element, string arrayPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(arrayPropertyName, out var array) || array.GetArrayLength() == 0)
        {
            return null;
        }

        var first = array[0];
        return first.TryGetProperty(propertyName, out var propertyElement) ? propertyElement.GetString() : null;
    }

    private static string NormalizeDisplayName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static StringContent CreateJsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static GoogleCursor ParseCursor(string cursor)
    {
        using var document = JsonDocument.Parse(cursor);
        var root = document.RootElement;
        return new GoogleCursor(
            root.TryGetProperty("syncToken", out var syncTokenElement) ? syncTokenElement.GetString() : null,
            root.TryGetProperty("pageToken", out var pageTokenElement) ? pageTokenElement.GetString() : null,
            root.TryGetProperty("requestSyncToken", out var requestSyncTokenElement) && requestSyncTokenElement.ValueKind == JsonValueKind.True);
    }

    private static string SerializeCursor(GoogleCursor cursor) =>
        new JsonObject
        {
            ["syncToken"] = cursor.SyncToken,
            ["pageToken"] = cursor.PageToken,
            ["requestSyncToken"] = cursor.RequestSyncToken,
        }.ToJsonString();

    private sealed record ConnectorPage(IReadOnlyList<CanonicalContact> Items, string? Cursor, bool HasMore);

    private sealed record GoogleCursor(string? SyncToken, string? PageToken, bool RequestSyncToken);

    private static JsonArray CreateArray(params JsonNode?[] items)
    {
        JsonArray array = [];
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }

    private static JsonArray CreateArray(IEnumerable<JsonNode?> items)
    {
        JsonArray array = [];
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
