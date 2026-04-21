
using System.Net;
using System.Text;

using Azure.Core;

using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors.Microsoft;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

/// <summary>
/// Tests for Microsoft Graph contact phone number mapping.
/// </summary>
public sealed class MicrosoftContactsConnectorTests
{
    // ── Phone type mapping ──────────────────────────────────────────────────

    [Theory]
    [InlineData("business", "work")]
    [InlineData("Business", "work")]
    [InlineData("BUSINESS", "work")]
    [InlineData("home", "home")]
    [InlineData("mobile", "mobile")]
    [InlineData("other", "other")]
    [InlineData("assistant", "assistant")]
    [InlineData("homeFax", "homefax")]
    [InlineData("pager", "pager")]
    [InlineData(null, "other")]
    public void MapFromMicrosoftPhoneType_MapsCorrectly(string? microsoftType, string expectedCanonical)
    {
        Assert.Equal(expectedCanonical, MicrosoftContactsConnector.MapFromMicrosoftPhoneType(microsoftType));
    }

    [Theory]
    [InlineData("work", "business")]
    [InlineData("Work", "business")]
    [InlineData("WORK", "business")]
    [InlineData("home", "home")]
    [InlineData("mobile", "mobile")]
    [InlineData("other", "other")]
    [InlineData("assistant", "assistant")]
    [InlineData(null, "other")]
    public void MapToMicrosoftPhoneType_MapsCorrectly(string? canonicalLabel, string expectedMicrosoftType)
    {
        Assert.Equal(expectedMicrosoftType, MicrosoftContactsConnector.MapToMicrosoftPhoneType(canonicalLabel));
    }

    // ── Beta phones collection reading ─────────────────────────────────────

    [Fact]
    public async Task GetItemAsync_TwoMobilePhones_BothSynced()
    {
        // Two "mobile" phones in the beta `phones` collection.
        string responseJson = BuildContactJson("contact-1", "Alice Smith",
            phones: [("555-0001", "mobile"), ("555-0002", "mobile")]);

        var connector = CreateConnector(responseJson);
        var item = await connector.GetItemAsync("contact-1");

        var contact = Assert.IsType<CanonicalContact>(item!.Payload);
        var mobiles = contact.Phones.Where(p => p.Label == "mobile").ToList();
        Assert.Equal(2, mobiles.Count);
        Assert.Contains(mobiles, p => p.Number == "555-0001");
        Assert.Contains(mobiles, p => p.Number == "555-0002");
    }

    [Fact]
    public async Task GetItemAsync_BetaPhonesCollection_MapsBusinessToWork()
    {
        // "business" type in the beta API should arrive as "work" in the canonical model.
        string responseJson = BuildContactJson("contact-2", "Bob Jones",
            phones: [("555-9999", "business")]);

        var connector = CreateConnector(responseJson);
        var item = await connector.GetItemAsync("contact-2");

        var contact = Assert.IsType<CanonicalContact>(item!.Payload);
        var phone = Assert.Single(contact.Phones);
        Assert.Equal("work", phone.Label);
        Assert.Equal("555-9999", phone.Number);
    }

    [Fact]
    public async Task GetItemAsync_BetaPhonesCollection_MixedLabels_AllPreserved()
    {
        // One work, one home, two mobile – all four must survive the round-trip.
        string responseJson = BuildContactJson("contact-3", "Carol White",
            phones:
            [
                ("555-0001", "business"),
                ("555-0002", "home"),
                ("555-0003", "mobile"),
                ("555-0004", "mobile"),
            ]);

        var connector = CreateConnector(responseJson);
        var item = await connector.GetItemAsync("contact-3");

        var contact = Assert.IsType<CanonicalContact>(item!.Payload);
        Assert.Equal(4, contact.Phones.Count);
        Assert.Single(contact.Phones, p => p.Label == "work" && p.Number == "555-0001");
        Assert.Single(contact.Phones, p => p.Label == "home" && p.Number == "555-0002");
        Assert.Equal(2, contact.Phones.Count(p => p.Label == "mobile"));
    }

    [Fact]
    public async Task GetItemAsync_V1FieldsFallback_WhenNoBetaPhonesProperty()
    {
        // When the `phones` property is absent the connector falls back to the v1.0 fields.
        string responseJson = BuildContactJsonV1("contact-4", "Dave Brown",
            businessPhones: ["555-1111", "555-2222"],
            homePhones: ["555-3333"],
            mobilePhone: "555-4444");

        var connector = CreateConnector(responseJson);
        var item = await connector.GetItemAsync("contact-4");

        var contact = Assert.IsType<CanonicalContact>(item!.Payload);
        Assert.Equal(4, contact.Phones.Count);
        Assert.Equal(2, contact.Phones.Count(p => p.Label == "work"));
        Assert.Single(contact.Phones, p => p.Label == "home");
        Assert.Single(contact.Phones, p => p.Label == "mobile");
    }

    [Fact]
    public async Task GetItemAsync_EmptyPhonesArray_ResultsInNoPhones()
    {
        string responseJson = BuildContactJson("contact-5", "Eve Green", phones: []);

        var connector = CreateConnector(responseJson);
        var item = await connector.GetItemAsync("contact-5");

        var contact = Assert.IsType<CanonicalContact>(item!.Payload);
        Assert.Empty(contact.Phones);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal contact JSON response that includes the beta <c>phones</c> array.
    /// </summary>
    private static string BuildContactJson(
        string id,
        string displayName,
        IEnumerable<(string number, string type)> phones)
    {
        string[] phonesArray = phones
            .Select(p => $@"{{""number"":""{p.number}"",""type"":""{p.type}""}}")
            .ToArray();

        return $$"""
            {
                "id": "{{id}}",
                "displayName": "{{displayName}}",
                "@odata.etag": "etag-1",
                "phones": [{{string.Join(",", phonesArray)}}]
            }
            """;
    }

    /// <summary>
    /// Builds a minimal contact JSON response that uses only the v1.0 phone fields
    /// (no <c>phones</c> property) to exercise the fallback path.
    /// </summary>
    private static string BuildContactJsonV1(
        string id,
        string displayName,
        string[] businessPhones,
        string[] homePhones,
        string? mobilePhone)
    {
        string business = string.Join(",", businessPhones.Select(p => $@"""{p}"""));
        string home = string.Join(",", homePhones.Select(p => $@"""{p}"""));
        string mobile = mobilePhone is null ? "null" : $@"""{mobilePhone}""";

        return $$"""
            {
                "id": "{{id}}",
                "displayName": "{{displayName}}",
                "@odata.etag": "etag-1",
                "businessPhones": [{{business}}],
                "homePhones": [{{home}}],
                "mobilePhone": {{mobile}}
            }
            """;
    }

    private static MicrosoftContactsConnector CreateConnector(string contactJson)
    {
        // Wrap in the same single-contact GET response shape the connector uses for GetItemAsync.
        string fullJson = contactJson;

        var handler = new StaticJsonHandler(fullJson);
        var httpClient = new HttpClient(handler);
        var credential = new MicrosoftClientCredential(new FakeTokenCredential());
        var endpoint = new EndpointOptions
        {
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["userId"] = "test-user",
            },
        };

        return new MicrosoftContactsConnector(
            httpClient,
            "test",
            endpoint,
            credential,
            NullLogger<MicrosoftContactsConnector>.Instance);
    }

    // ── Test doubles ───────────────────────────────────────────────────────

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Handle the photo endpoint that GetItemAsync calls after the main request.
            if (request.RequestUri?.AbsolutePath.EndsWith("/photo/$value", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
