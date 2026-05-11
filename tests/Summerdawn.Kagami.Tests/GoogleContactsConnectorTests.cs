using System.Text.Json;
using System.Text.Json.Nodes;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

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
        var contact = GoogleContactsConnector.ConvertPerson(document.RootElement, groupNamesByResource, "google");

        Assert.NotNull(contact);
        string actual = SerializeCore(contact);
        string expected = """{"givenName":"Adriana","middleName":null,"familyName":"De Matteis","displayName":"Adriana De Matteis","emails":[],"phones":[{"label":"mobile","number":"+41794318938"}],"addresses":[],"organization":null,"title":null,"notes":"- Met 2024-02-03 at Heavenly Heat","categories":["Heavenly Heat"],"birthday":null}""";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConvertPerson_UsesSentinelYearForYearlessBirthday()
    {
        string personJson = """
            {
                "resourceName": "people/123",
                "names": [{"displayName": "Birthday Contact"}],
                "birthdays": [{"date": {"month": 5, "day": 10}}]
            }
            """;

        using var document = JsonDocument.Parse(personJson);
        var contact = GoogleContactsConnector.ConvertPerson(document.RootElement, new Dictionary<string, string>(), "google");

        Assert.NotNull(contact);
        Assert.Equal(new DateOnly(1604, 5, 10), contact!.Birthday);
    }

    [Fact]
    public void ConvertPerson_UnknownCustomGroupIsSkipped()
    {
        // When a membership resource name is not in the
        // supplied mapping, the membership should be skipped.
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
            new Dictionary<string, string>(),
            "google");

        Assert.NotNull(contact);
        // myContacts filtered; unknown custom group skipped.
        Assert.Empty(contact.Categories);
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
            new Dictionary<string, string>(),
            "google");

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
        var resourceNames = ReadMembershipResourceNames(memberships!.AsArray());
        Assert.Equal(2, resourceNames.Count);
        Assert.Contains("contactGroups/myContacts", resourceNames);
        Assert.Contains("contactGroups/357e2b95895a4962", resourceNames);
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
        var resourceNames = ReadMembershipResourceNames(memberships!.AsArray());
        Assert.Equal(2, resourceNames.Count);
        Assert.Contains("contactGroups/myContacts", resourceNames);
        Assert.Contains("contactGroups/357e2b95895a4962", resourceNames);
        Assert.DoesNotContain("contactGroups/starred", resourceNames);
    }

    [Fact]
    public void BuildWritablePerson_NoCategories_AddsMyContactsMembership()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Empty",
            Provenance = { ProviderId = "people/999" },
        };

        var person = GoogleContactsConnector.BuildWritablePerson(contact, new Dictionary<string, string>());

        Assert.True(person.TryGetPropertyValue("memberships", out var memberships));
        var resourceNames = ReadMembershipResourceNames(memberships!.AsArray());
        Assert.Single(resourceNames);
        Assert.Equal("contactGroups/myContacts", resourceNames[0]);
    }

    [Fact]
    public void MergeExistingSystemMemberships_PreservesStarredAndDropsOldCustomMemberships()
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

        var writablePerson = GoogleContactsConnector.BuildWritablePerson(contact, groupNamesByResource);

        string existingPersonJson = """
            {
                "resourceName": "people/123",
                "memberships": [
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/myContacts"}},
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/starred"}},
                    {"contactGroupMembership": {"contactGroupResourceName": "contactGroups/old-group"}}
                ]
            }
            """;

        using var existingPersonDocument = JsonDocument.Parse(existingPersonJson);
        GoogleContactsConnector.MergeExistingSystemMemberships(writablePerson, existingPersonDocument.RootElement);

        Assert.True(writablePerson.TryGetPropertyValue("memberships", out var memberships));
        var resourceNames = ReadMembershipResourceNames(memberships!.AsArray());
        Assert.Equal(3, resourceNames.Count);
        Assert.Contains("contactGroups/myContacts", resourceNames);
        Assert.Contains("contactGroups/starred", resourceNames);
        Assert.Contains("contactGroups/357e2b95895a4962", resourceNames);
        Assert.DoesNotContain("contactGroups/old-group", resourceNames);
    }

    [Fact]
    public void BuildWritablePerson_OmitsYearForSentinelBirthday()
    {
        var contact = new CanonicalContact
        {
            DisplayName = "Birthday Contact",
            Birthday = new DateOnly(1604, 5, 10),
            Provenance = { ProviderId = "people/123" },
        };

        var person = GoogleContactsConnector.BuildWritablePerson(contact, new Dictionary<string, string>());

        Assert.True(person.TryGetPropertyValue("birthdays", out var birthdays));
        var birthdayDate = birthdays!.AsArray()[0]!["date"]!.AsObject();
        Assert.False(birthdayDate.ContainsKey("year"));
        Assert.Equal(5, birthdayDate["month"]!.GetValue<int>());
        Assert.Equal(10, birthdayDate["day"]!.GetValue<int>());
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static string SerializeCore(CanonicalContact contact) =>
        CanonicalContactTestHelpers.SerializeCore(contact);

    private static List<string> ReadMembershipResourceNames(JsonArray memberships) =>
        memberships
            .Select(node => node?["contactGroupMembership"]?["contactGroupResourceName"]?.GetValue<string>())
            .Where(resourceName => !string.IsNullOrWhiteSpace(resourceName))
            .Select(resourceName => resourceName!)
            .ToList();
}
