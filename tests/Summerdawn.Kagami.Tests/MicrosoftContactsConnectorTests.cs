using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class MicrosoftContactsConnectorTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static MicrosoftContactsConnector CreateConnector(params string[] responseJsons)
    {
        var handler = new FakeHttpHandler(responseJsons);
        var httpClient = new HttpClient(handler);
        var endpoint = new EndpointOptions
        {
            Type = EndpointOptions.MicrosoftContacts,
            Properties = new Dictionary<string, string> { ["userId"] = "user@example.com" },
        };
        var credential = new MicrosoftClientCredential(new FakeTokenCredential());
        return new MicrosoftContactsConnector(httpClient, "test", endpoint, credential);
    }

    private static string ContactJson(
        string id,
        string? displayName = null,
        string? mobile = null,
        IEnumerable<string>? businessPhones = null,
        IEnumerable<string>? homePhones = null,
        DateTimeOffset? lastModified = null,
        IEnumerable<(string id, string? value)>? extendedProps = null)
    {
        var obj = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName ?? id,
            ["givenName"] = null,
            ["middleName"] = null,
            ["surname"] = null,
            ["emailAddresses"] = new JsonArray(),
            ["businessPhones"] = CreateStringArray(businessPhones ?? []),
            ["homePhones"] = CreateStringArray(homePhones ?? []),
            ["mobilePhone"] = mobile,
            ["companyName"] = null,
            ["jobTitle"] = null,
            ["personalNotes"] = null,
            ["birthday"] = null,
            ["categories"] = new JsonArray(),
            ["homeAddress"] = null,
            ["businessAddress"] = null,
            ["otherAddress"] = null,
            ["lastModifiedDateTime"] = (lastModified ?? DateTimeOffset.UtcNow).ToString("O"),
        };

        if (extendedProps is not null)
        {
            var array = new JsonArray();
            foreach (var (propId, val) in extendedProps)
            {
                array.Add(new JsonObject { ["id"] = propId, ["value"] = val });
            }
            obj["singleValueExtendedProperties"] = array;
        }

        return obj.ToJsonString();
    }

    private static string DeltaPage(string deltaLink, params string[] contactJsons) =>
        $"{{\"value\":[{string.Join(",", contactJsons)}],\"@odata.deltaLink\":\"{deltaLink}\"}}";

    private static string BatchResponse(params string[] contactJsons)
    {
        var responses = new JsonArray();
        for (int i = 0; i < contactJsons.Length; i++)
        {
            responses.Add(new JsonObject
            {
                ["id"] = (i + 1).ToString(),
                ["status"] = 200,
                ["body"] = JsonNode.Parse(contactJsons[i]),
            });
        }

        return new JsonObject
        {
            ["responses"] = responses,
        }.ToJsonString();
    }

    private static string ThrottledBatchResponse(string retryAfterSeconds, params string[] contactJsons)
    {
        var responses = new JsonArray();
        responses.Add(new JsonObject
        {
            ["id"] = "1",
            ["status"] = 429,
            ["headers"] = new JsonObject { ["Retry-After"] = retryAfterSeconds },
            ["body"] = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "ApplicationThrottled",
                    ["message"] = "Application is over its MailboxConcurrency limit.",
                }
            },
        });

        for (int i = 0; i < contactJsons.Length; i++)
        {
            responses.Add(new JsonObject
            {
                ["id"] = (i + 2).ToString(),
                ["status"] = 200,
                ["body"] = JsonNode.Parse(contactJsons[i]),
            });
        }

        return new JsonObject
        {
            ["responses"] = responses,
        }.ToJsonString();
    }

    private static JsonArray CreateStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values)
        {
            array.Add((JsonNode?)JsonValue.Create(v));
        }
        return array;
    }

    // ── read: native phone fields unchanged ───────────────────────────────

    [Fact]
    public async Task GetInitialPage_MapsBusinessPhonesAsWork()
    {
        string contact = ContactJson("c1", businessPhones: ["555-0001", "555-0002"]);
        string enrichment = BatchResponse(ContactJson("c1", businessPhones: ["555-0001", "555-0002"]));
        var connector = CreateConnector(
            (DeltaPage("delta1", contact)),
            (enrichment));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Equal(2, phones.Count);
        Assert.All(phones, p => Assert.Equal("work", p.Label));
        Assert.Contains(phones, p => p.Number == "555-0001");
        Assert.Contains(phones, p => p.Number == "555-0002");
    }

    [Fact]
    public async Task GetAllItemsAsync_LoadsContactsDirectlyWithoutBatchHydration()
    {
        string page = "{" +
            "\"value\":[" + ContactJson("c1", businessPhones: ["555-0001"], extendedProps: [("String 0x3A1F", "555-other")]) + "]}";
        var connector = CreateConnector(page);

        var items = await connector.GetAllItemsAsync();

        var contact = items.Single();
        Assert.Equal("c1", contact.Provenance.ProviderId);
        Assert.Contains(contact.Phones, phone => phone.Label == "work" && phone.Number == "555-0001");
        Assert.Contains(contact.Phones, phone => phone.Label == "other" && phone.Number == "555-other");
    }

    [Fact]
    public async Task GetInitialPage_MapsHomePhonesAsHome()
    {
        string contact = ContactJson("c1", homePhones: ["555-1001"]);
        string enrichment = BatchResponse(ContactJson("c1", homePhones: ["555-1001"]));
        var connector = CreateConnector(
            (DeltaPage("delta1", contact)),
            (enrichment));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Single(phones);
        Assert.Equal("home", phones[0].Label);
        Assert.Equal("555-1001", phones[0].Number);
    }

    [Fact]
    public async Task GetInitialPage_MapsMobilePhone()
    {
        string contact = ContactJson("c1", mobile: "555-2001");
        string enrichment = BatchResponse(ContactJson("c1", mobile: "555-2001"));
        var connector = CreateConnector(
            (DeltaPage("delta1", contact)),
            (enrichment));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Single(phones);
        Assert.Equal("mobile", phones[0].Label);
        Assert.Equal("555-2001", phones[0].Number);
    }

    // ── read: extended phone properties ───────────────────────────────────

    [Fact]
    public async Task GetInitialPage_MapsExtendedOtherPhone()
    {
        string deltaContact = ContactJson("c1");
        string enriched = ContactJson("c1", extendedProps: [("String 0x3A1F", "555-9001")]);
        var connector = CreateConnector(
            (DeltaPage("delta1", deltaContact)),
            (BatchResponse(enriched)));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Single(phones);
        Assert.Equal("other", phones[0].Label);
        Assert.Equal("555-9001", phones[0].Number);
    }

    [Fact]
    public async Task GetInitialPage_MapsAllExtendedPhoneLabels()
    {
        string deltaContact = ContactJson("c1");
        string enriched = ContactJson("c1", extendedProps:
        [
            ("String 0x3A1F", "555-other"),
            ("String 0x3A21", "555-pager"),
            ("String 0x3A1D", "555-radio"),
            ("String 0x3A2E", "555-assistant"),
            ("String 0x3A57", "555-main"),
        ]);
        var connector = CreateConnector(
            (DeltaPage("delta1", deltaContact)),
            (BatchResponse(enriched)));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Equal(5, phones.Count);
        Assert.Contains(phones, p => p.Label == "other" && p.Number == "555-other");
        Assert.Contains(phones, p => p.Label == "pager" && p.Number == "555-pager");
        Assert.Contains(phones, p => p.Label == "radio" && p.Number == "555-radio");
        Assert.Contains(phones, p => p.Label == "assistant" && p.Number == "555-assistant");
        Assert.Contains(phones, p => p.Label == "main" && p.Number == "555-main");
    }

    // This test validates that the connector itself surfaces a clear error when a throttled
    // batch response reaches it (e.g. when the handler pipeline is bypassed in tests).
    // Promotion of the outer response to 429 is covered by MicrosoftGraphBatchThrottleHandlerTests.
    [Fact]
    public async Task GetInitialPage_ThrottledBatchSubResponse_ThrowsInvalidOperationException()
    {
        string deltaContact = ContactJson("c1");
        string enrichment = ThrottledBatchResponse("6", ContactJson("c1", extendedProps: [("String 0x3A1F", "555-other")]));
        var connector = CreateConnector(
            (DeltaPage("delta1", deltaContact)),
            (enrichment));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.GetCursorItemsAsync(cursor: null));

        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task GetInitialPage_EmptyStringExtendedProperty_TreatedAsAbsent()
    {
        string deltaContact = ContactJson("c1");
        string enriched = ContactJson("c1", extendedProps:
        [
            ("String 0x3A1F", ""),         // empty
            ("String 0x3A21", "   "),      // whitespace
            ("String 0x3A1D", "555-radio"),
        ]);
        var connector = CreateConnector(
            (DeltaPage("delta1", deltaContact)),
            (BatchResponse(enriched)));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Single(phones);
        Assert.Equal("radio", phones[0].Label);
    }

    [Fact]
    public async Task GetInitialPage_MissingExtendedProperty_TreatedAsAbsent()
    {
        string deltaContact = ContactJson("c1");
        // No singleValueExtendedProperties at all
        string enriched = ContactJson("c1");
        var connector = CreateConnector(
            (DeltaPage("delta1", deltaContact)),
            (BatchResponse(enriched)));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        var phones = GetContact(page, "c1").Phones;
        Assert.Empty(phones);
    }

    // ── read: enrichment behavior ─────────────────────────────────────────

    [Fact]
    public async Task GetInitialPage_ExtraContactsInEnrichmentQuery_AreIgnored()
    {
        string deltaContact = ContactJson("c1");
        string enrichedC1 = ContactJson("c1", extendedProps: [("String 0x3A1F", "555-other")]);
        string enrichedExtra = ContactJson("c-extra", extendedProps: [("String 0x3A1F", "555-extra")]);

        var connector = CreateConnector(
            (DeltaPage("delta1", deltaContact)),
            (BatchResponse(enrichedC1, enrichedExtra)));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        // Only c1 should be in the page, not c-extra
        Assert.Single(page.Items);
        Assert.Equal("c1", page.Items[0].Provenance.ProviderId);
    }

    [Fact]
    public async Task GetInitialPage_DeletedContacts_NotEnrichedFromFilteredQuery()
    {
        // A deleted contact plus a non-deleted contact
        string deleted = "{\"id\":\"del1\",\"@removed\":{\"reason\":\"deleted\"},\"lastModifiedDateTime\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}";
        string nonDeleted = ContactJson("c1", extendedProps: [("String 0x3A1F", "555-other")]);
        string deltaPage = $"{{\"value\":[{deleted},{ContactJson("c1")}],\"@odata.deltaLink\":\"delta1\"}}";

        var connector = CreateConnector(
            (deltaPage),
            (BatchResponse(nonDeleted)));

        var page = await connector.GetCursorItemsAsync(cursor: null);

        Assert.Equal(2, page.Items.Count);
        var deletedItem = page.Items.Single(i => i.Provenance.ProviderId == "del1");
        Assert.True(deletedItem.IsDeleted);
        var liveItem = page.Items.Single(i => i.Provenance.ProviderId == "c1");
        Assert.Single(liveItem.Phones);
        Assert.Equal("other", liveItem.Phones[0].Label);
    }

    // ── write: extended phone properties ─────────────────────────────────

    [Fact]
    public void BuildWritableContact_AlwaysIncludesFullExtendedPropertySet()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Test",

            Provenance =
            {
                ProviderId = "test"
            }
        };
        var json = InvokeInternalBuildWritableContact(contact);

        Assert.True(json.TryGetPropertyValue("singleValueExtendedProperties", out var extProps));
        var array = extProps!.AsArray();
        // Should have all 5 extended phone properties
        Assert.Equal(5, array.Count);

        var ids = array.Select(n => n!["id"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("String 0x3A1F", ids);
        Assert.Contains("String 0x3A21", ids);
        Assert.Contains("String 0x3A1D", ids);
        Assert.Contains("String 0x3A2E", ids);
        Assert.Contains("String 0x3A57", ids);
    }

    [Fact]
    public void BuildWritableContact_WritesNullForAbsentExtendedProperties()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Test",

            Provenance =
            {
                ProviderId = "test"
            }
        };
        var json = InvokeInternalBuildWritableContact(contact);

        var extProps = json["singleValueExtendedProperties"]!.AsArray();
        Assert.All(extProps, prop => Assert.Null(prop!["value"]?.GetValue<string?>()));
    }

    [Fact]
    public void BuildWritableContact_WritesValueForPresentExtendedProperties()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Test",
            Phones =
            {
                new ContactPhone { Label = "other", Number = "555-9001" },
                new ContactPhone { Label = "pager", Number = "555-9002" },
                new ContactPhone { Label = "assistant", Number = "555-9003" },
            },

            Provenance =
            {
                ProviderId = "test"
            }
        };
        var json = InvokeInternalBuildWritableContact(contact);

        var extProps = json["singleValueExtendedProperties"]!.AsArray();
        var byId = extProps.ToDictionary(
            n => n!["id"]!.GetValue<string>(),
            n => n!["value"]?.GetValue<string?>());

        Assert.Equal("555-9001", byId["String 0x3A1F"]);
        Assert.Equal("555-9002", byId["String 0x3A21"]);
        Assert.Null(byId["String 0x3A1D"]);    // radio: absent
        Assert.Equal("555-9003", byId["String 0x3A2E"]);
        Assert.Null(byId["String 0x3A57"]);    // main: absent
    }

    [Fact]
    public void BuildWritableContact_PreservesNativePhoneFields()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Test",
            Phones =
            {
                new ContactPhone { Label = "work", Number = "555-0001" },
                new ContactPhone { Label = "home", Number = "555-1001" },
                new ContactPhone { Label = "mobile", Number = "555-2001" },
            },

            Provenance =
            {
                ProviderId = "test"
            }
        };
        var json = InvokeInternalBuildWritableContact(contact);

        var biz = json["businessPhones"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["555-0001"], biz);

        var home = json["homePhones"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["555-1001"], home);

        Assert.Equal("555-2001", json["mobilePhone"]!.GetValue<string>());
    }

    // ── normalization: direct ConvertContact tests ────────────────────────

    [Fact]
    public void ConvertContact_NormalizesEmptyStringsToNull()
    {
        // Representative Microsoft Graph contact with empty-string fields and all-null address objects.
        string contactJson = """
            {
                "id": "test-id",
                "givenName": "Yvonne",
                "middleName": "",
                "surname": "Pignolet",
                "displayName": "Yvonne Pignolet",
                "emailAddresses": [],
                "businessPhones": [],
                "homePhones": [],
                "mobilePhone": "+41 78 743 07 46",
                "companyName": null,
                "jobTitle": "",
                "personalNotes": "- Some notes",
                "birthday": null,
                "categories": [],
                "homeAddress": null,
                "businessAddress": null,
                "otherAddress": null,
                "lastModifiedDateTime": "2024-01-01T00:00:00Z"
            }
            """;

        using var document = JsonDocument.Parse(contactJson);
        var contact = MicrosoftContactsConnector.ConvertContact(document.RootElement);

        Assert.NotNull(contact);
        string actual = SerializeCore(contact);
        string expected = """{"givenName":"Yvonne","middleName":null,"familyName":"Pignolet","displayName":"Yvonne Pignolet","emails":[],"phones":[{"label":"mobile","number":"+41 78 743 07 46"}],"addresses":[],"organization":null,"title":null,"notes":"- Some notes","categories":[],"birthday":null}""";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertContact_DropsAddressesWhereAllMeaningfulFieldsAreNull()
    {
        // All three address objects are present but contain only null fields.
        string contactJson = """
            {
                "id": "test-id",
                "displayName": "Adriana De Matteis",
                "emailAddresses": [],
                "businessPhones": [],
                "homePhones": [],
                "mobilePhone": "+41794318938",
                "birthday": null,
                "categories": ["Heavenly Heat"],
                "homeAddress":     {"street": null, "city": null, "state": null, "postalCode": null, "countryOrRegion": null},
                "businessAddress": {"street": null, "city": null, "state": null, "postalCode": null, "countryOrRegion": null},
                "otherAddress":    {"street": null, "city": null, "state": null, "postalCode": null, "countryOrRegion": null},
                "lastModifiedDateTime": "2024-01-01T00:00:00Z"
            }
            """;

        using var document = JsonDocument.Parse(contactJson);
        var contact = MicrosoftContactsConnector.ConvertContact(document.RootElement);

        Assert.NotNull(contact);
        string actual = SerializeCore(contact);
        string expected = """{"givenName":null,"middleName":null,"familyName":null,"displayName":"Adriana De Matteis","emails":[],"phones":[{"label":"mobile","number":"+41794318938"}],"addresses":[],"organization":null,"title":null,"notes":null,"categories":["Heavenly Heat"],"birthday":null}""";
        Assert.Equal(expected, actual);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static CanonicalContact GetContact(ItemSet<CanonicalContact> page, string id) =>
        page.Items.Single(i => i.Provenance.ProviderId == id);

    /// <summary>
    /// Calls <see cref="MicrosoftContactsConnector.BuildWritableContact"/> directly (now internal).
    /// </summary>
    private static JsonObject InvokeInternalBuildWritableContact(CanonicalContact contact) =>
        MicrosoftContactsConnector.BuildWritableContact(contact);

    private static string SerializeCore(CanonicalContact contact) =>
        CanonicalContactTestHelpers.SerializeCore(contact);

    // ── HTTP stubs ────────────────────────────────────────────────────────

    private sealed class FakeHttpHandler(params string[] responseJsons) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(responseJsons);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/photo/$value", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Return 404 for photo requests so photos are skipped.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (!responses.TryDequeue(out string? json))
            {
                throw new InvalidOperationException($"Unexpected HTTP request to {request.RequestUri}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
