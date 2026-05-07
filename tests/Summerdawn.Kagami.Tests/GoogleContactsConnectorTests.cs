using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Tests;

public sealed class GoogleContactsConnectorTests
{
    // ── ConvertPerson ─────────────────────────────────────────────────────

    [Fact]
    public void ConvertPerson_FiltersSystemMembershipsAndTranslatesCustomGroup()
    {
        // Representative People API person with myContacts, starred, and a custom group.
        string personJson = """
            {
                "resourceName": "people/123",
                "etag": "etag-abc",
                "names": [{"givenName": "Adriana", "familyName": "De Matteis", "displayName": "Adriana De Matteis"}],
                "phoneNumbers": [{"value": "+41794318938", "type": "mobile"}],
                "biographies": [{"value": "- Met 2024-02-03 at Heavenly Heat", "contentType": "TEXT_PLAIN"}],
                "memberships": [
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/myContacts"}},
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/starred"}},
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/357e2b95895a4962"}}
                ]
            }
            """;

        var groupNamesByResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contactGroups/357e2b95895a4962"] = "Heavenly Heat",
        };

        using var document = JsonDocument.Parse(personJson);
        var contact = GoogleContactsConnector.ConvertPerson(document.RootElement, groupNamesByResource);

        Assert.NotNull(contact);
        string actual = SerializeCore(contact);
        string expected = """{"givenName":"Adriana","middleName":null,"familyName":"De Matteis","displayName":"Adriana De Matteis","emails":[],"phones":[{"label":"mobile","number":"+41794318938"}],"addresses":[],"organization":null,"title":null,"notes":"- Met 2024-02-03 at Heavenly Heat","categories":["Heavenly Heat"],"birthday":null}""";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertPerson_UnknownCustomGroupFallsBackToRawResourceName()
    {
        // When a membership resource name is not in the supplied mapping,
        // the raw resource name should be preserved instead of being silently dropped.
        string personJson = """
            {
                "resourceName": "people/456",
                "names": [{"displayName": "Test Contact"}],
                "memberships": [
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/myContacts"}},
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/unknown-abc"}}
                ]
            }
            """;

        // Empty mapping — the custom group is unknown.
        using var document = JsonDocument.Parse(personJson);
        var contact = GoogleContactsConnector.ConvertPerson(
            document.RootElement,
            new Dictionary<string, string>());

        Assert.NotNull(contact);
        // myContacts filtered; unknown custom group preserved as raw resource name.
        string category = Assert.Single(contact.Categories);
        Assert.Equal("contactGroups/unknown-abc", category);
    }

    [Fact]
    public void ConvertPerson_OnlySystemMemberships_ProducesEmptyCategories()
    {
        string personJson = """
            {
                "resourceName": "people/789",
                "names": [{"displayName": "Simple Contact"}],
                "memberships": [
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/myContacts"}},
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/starred"}}
                ]
            }
            """;

        using var document = JsonDocument.Parse(personJson);
        var contact = GoogleContactsConnector.ConvertPerson(
            document.RootElement,
            new Dictionary<string, string>());

        Assert.NotNull(contact);
        Assert.Empty(contact.Categories);
    }

    // ── BuildWritablePerson ───────────────────────────────────────────────

    [Fact]
    public void BuildWritablePerson_ResolvesCustomCategoryToResourceName()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Adriana De Matteis",
            Categories = ["Heavenly Heat"],
            Provenance = { ProviderId = "people/123" },
        };

        var groupNamesByResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contactGroups/357e2b95895a4962"] = "Heavenly Heat",
        };

        var person = GoogleContactsConnector.BuildWritablePerson(contact, groupNamesByResource);

        Assert.True(person.TryGetPropertyValue("memberships", out var memberships));
        var array = memberships!.AsArray();
        Assert.Single(array);
        string resourceName = array[0]!["contactGroupMembership"]!["contactGroupResourceName"]!.GetValue<string>();
        Assert.Equal("contactGroups/357e2b95895a4962", resourceName);
    }

    [Fact]
    public void BuildWritablePerson_DoesNotEmitSystemGroupsEvenWhenPresentAsCategories()
    {
        // Even if "My Contacts" / "Starred" appear as canonical categories (e.g. from a full cache
        // where those system groups are mapped), they must not be written back as memberships.
        var contact = new CanonicalContact
        {
            DisplayName = "Test",
            Categories = ["My Contacts", "Starred", "Heavenly Heat"],
            Provenance = { ProviderId = "people/789" },
        };

        var groupNamesByResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contactGroups/myContacts"] = "My Contacts",
            ["contactGroups/starred"] = "Starred",
            ["contactGroups/357e2b95895a4962"] = "Heavenly Heat",
        };

        var person = GoogleContactsConnector.BuildWritablePerson(contact, groupNamesByResource);

        Assert.True(person.TryGetPropertyValue("memberships", out var memberships));
        var array = memberships!.AsArray();
        // Only the custom group should appear — the two system groups are filtered.
        Assert.Single(array);
        string resourceName = array[0]!["contactGroupMembership"]!["contactGroupResourceName"]!.GetValue<string>();
        Assert.Equal("contactGroups/357e2b95895a4962", resourceName);
    }

    [Fact]
    public void BuildWritablePerson_NoCategories_OmitsMemberships()
    {
        // When the contact has no categories, the memberships key should be absent entirely
        // (myContacts must not be injected).
        var contact = new CanonicalContact
        {
            DisplayName = "Empty",
            Provenance = { ProviderId = "people/999" },
        };

        var person = GoogleContactsConnector.BuildWritablePerson(contact, new Dictionary<string, string>());

        Assert.False(person.ContainsKey("memberships"));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="contact"/> to a compact camelCase JSON string, excluding provenance,
    /// for stable test assertions.
    /// </summary>
    private static string SerializeCore(CanonicalContact contact)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new KagamiJsonContext(),
        };
        var node = JsonNode.Parse(JsonSerializer.Serialize(contact, options))!.AsObject();
        node.Remove("provenance");
        return node.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
