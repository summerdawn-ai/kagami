namespace Summerdawn.Kagami.Connectors.Graph;

using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;

internal sealed class GraphContactsConnector : IConnector
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string ContactSelectFields = "id,displayName,givenName,middleName,surname,emailAddresses,businessPhones,homePhones,mobilePhone,companyName,jobTitle,personalNotes,birthday,categories,homeAddress,businessAddress,otherAddress,lastModifiedDateTime";

    private readonly HttpClient httpClient;
    private readonly TokenCredential credential;
    private readonly string collectionPath;

    public GraphContactsConnector(
        HttpClient httpClient,
        string endpointName,
        EndpointOptions endpoint,
        CredentialOptions credential,
        ILogger<GraphContactsConnector> logger)
    {
        _ = endpointName;
        _ = logger;
        this.httpClient = httpClient;
        if (!string.IsNullOrWhiteSpace(credential.Type)
            && !string.Equals(credential.Type, "graph-client-credentials", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Graph contacts endpoint '{endpointName}' requires credential type 'graph-client-credentials'.");
        }

        this.credential = CreateCredential(credential.Properties);
        string userId = endpoint.Properties.GetRequiredValue("userId", $"endpoint '{endpointName}'");
        string? folderId = endpoint.Properties.GetOptionalValue("folderId");
        collectionPath = folderId is null
            ? $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}/contacts"
            : $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}/contactFolders/{Uri.EscapeDataString(folderId)}/contacts";
    }

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "graph-contacts",
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = false,
        SupportsRecurrence = false,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = true,
    };

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _ = await credential.GetTokenAsync(new TokenRequestContext([GraphScope]), cancellationToken);
    }

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        GetPageAsync($"{collectionPath}/delta?$select={Uri.EscapeDataString(ContactSelectFields)}", cancellationToken);

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
        GetPageAsync(cursor, cancellationToken);

    public async Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, $"{collectionPath}/{Uri.EscapeDataString(id)}", cancellationToken);
        using JsonDocument document = await SendForJsonAsync(request, cancellationToken);
        return ConvertContact(document.RootElement);
    }

    public async Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Post, collectionPath, cancellationToken);
        request.Content = CreateJsonContent(BuildWritableContact(GetContactPayload(item)));
        using JsonDocument document = await SendForJsonAsync(request, cancellationToken);
        return ConvertContact(document.RootElement) ?? throw new InvalidOperationException("Graph contact create returned no payload.");
    }

    public async Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Patch, $"{collectionPath}/{Uri.EscapeDataString(item.SourceId)}", cancellationToken);
        request.Content = CreateJsonContent(BuildWritableContact(GetContactPayload(item)));
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            request.Headers.TryAddWithoutValidation("If-Match", item.Version);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await GetItemAsync(item.SourceId, cancellationToken)
            ?? throw new InvalidOperationException("Graph contact update succeeded but the updated item could not be reloaded.");
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Delete, $"{collectionPath}/{Uri.EscapeDataString(id)}", cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<IncrementalPage> GetPageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, requestUri, cancellationToken);
        using JsonDocument document = await SendForJsonAsync(request, cancellationToken);

        List<CanonicalItem> items = [];
        if (document.RootElement.TryGetProperty("value", out JsonElement values))
        {
            foreach (JsonElement element in values.EnumerateArray())
            {
                CanonicalItem? item = ConvertContact(element);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        string? nextLink = document.RootElement.TryGetProperty("@odata.nextLink", out JsonElement nextLinkElement)
            ? nextLinkElement.GetString()
            : null;
        string? deltaLink = document.RootElement.TryGetProperty("@odata.deltaLink", out JsonElement deltaLinkElement)
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
        AccessToken token = await credential.GetTokenAsync(new TokenRequestContext([GraphScope]), cancellationToken);
        HttpRequestMessage request = new(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
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
        throw new InvalidOperationException($"Microsoft Graph request failed with {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static StringContent CreateJsonContent(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static object BuildWritableContact(CanonicalContact contact)
    {
        return new
        {
            givenName = contact.GivenName,
            middleName = contact.MiddleName,
            surname = contact.FamilyName,
            displayName = contact.DisplayName,
            emailAddresses = contact.Emails.Select(email => new
            {
                name = contact.DisplayName,
                address = email.Address,
            }).ToArray(),
            businessPhones = contact.Phones.Where(phone => string.Equals(phone.Label, "work", StringComparison.OrdinalIgnoreCase)).Select(phone => phone.Number).ToArray(),
            homePhones = contact.Phones.Where(phone => string.Equals(phone.Label, "home", StringComparison.OrdinalIgnoreCase)).Select(phone => phone.Number).ToArray(),
            mobilePhone = contact.Phones.FirstOrDefault(phone => string.Equals(phone.Label, "mobile", StringComparison.OrdinalIgnoreCase))?.Number,
            companyName = contact.Organization,
            jobTitle = contact.Title,
            personalNotes = contact.Notes,
            birthday = contact.Birthday?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            categories = contact.Categories,
            homeAddress = ToGraphAddress(contact.Addresses.FirstOrDefault(address => string.Equals(address.Label, "home", StringComparison.OrdinalIgnoreCase))),
            businessAddress = ToGraphAddress(contact.Addresses.FirstOrDefault(address => string.Equals(address.Label, "work", StringComparison.OrdinalIgnoreCase))),
            otherAddress = ToGraphAddress(contact.Addresses.FirstOrDefault(address => string.Equals(address.Label, "other", StringComparison.OrdinalIgnoreCase))),
        };
    }

    private static object? ToGraphAddress(ContactAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return new
        {
            street = address.Street,
            city = address.City,
            state = address.State,
            postalCode = address.PostalCode,
            countryOrRegion = address.Country,
        };
    }

    private static CanonicalContact GetContactPayload(CanonicalItem item) =>
        item.Payload as CanonicalContact
        ?? throw new InvalidOperationException("Microsoft Graph connector only supports CanonicalContact payloads.");

    private static CanonicalItem? ConvertContact(JsonElement element)
    {
        string? id = element.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
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

        if (element.TryGetProperty("emailAddresses", out JsonElement emailAddresses))
        {
            foreach (JsonElement email in emailAddresses.EnumerateArray())
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

        return CanonicalItemSerializer.WithComputedHash(new CanonicalItem
        {
            EntityType = EntityType.Contact,
            Payload = contact,
            SourceId = id,
            Version = ReadString(element, "@odata.etag"),
            Metadata = new Dictionary<string, string>(),
        });
    }

    private static void AddPhones(ICollection<ContactPhone> phones, JsonElement element, string propertyName, string label)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement values))
        {
            return;
        }

        foreach (JsonElement phone in values.EnumerateArray())
        {
            string? value = phone.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                phones.Add(new ContactPhone { Number = value, Label = label });
            }
        }
    }

    private static void AddAddress(ICollection<ContactAddress> addresses, JsonElement element, string propertyName, string label)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement addressElement) || addressElement.ValueKind != JsonValueKind.Object)
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
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
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
        return DateTimeOffset.TryParse(value, out DateTimeOffset dateTimeOffset) ? dateTimeOffset : null;
    }

    private static DateOnly? ReadDateOnly(JsonElement element, string propertyName)
    {
        string? value = ReadString(element, propertyName);
        if (DateTimeOffset.TryParse(value, out DateTimeOffset dateTimeOffset))
        {
            return DateOnly.FromDateTime(dateTimeOffset.UtcDateTime);
        }

        return null;
    }

    private static TokenCredential CreateCredential(IReadOnlyDictionary<string, string> properties)
    {
        string tenantId = properties.GetRequiredValue("tenantId", "graph credentials");
        string clientId = properties.GetRequiredValue("clientId", "graph credentials");
        string? clientSecret = properties.GetOptionalValue("clientSecret");
        string? certificatePath = properties.GetOptionalValue("certificatePath");
        string? certificatePassword = properties.GetOptionalValue("certificatePassword");

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            X509Certificate2 certificate = string.IsNullOrWhiteSpace(certificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword);
            return new ClientCertificateCredential(tenantId, clientId, certificate);
        }

        throw new InvalidOperationException("Graph credentials require either 'clientSecret' or 'certificatePath'.");
    }
}
