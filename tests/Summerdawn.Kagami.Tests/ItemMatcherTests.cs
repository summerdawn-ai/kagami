using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class ItemMatcherTests
{
    [Fact]
    public void IsMatch_NormalizesEmailAndPhone()
    {
        var left = CreateContact(
            "a1",
            "Alice Smith",
            organization: "Contoso",
            email: "Alice@Example.com ",
            phone: "+1 (555) 123-4567");
        var right = CreateContact(
            "b1",
            " alice smith ",
            organization: "Other Co",
            email: " alice@example.com",
            phone: "15551234567");

        Assert.True(ItemMatcher.IsMatch(left, right));
    }

    [Fact]
    public void IsMatch_RequiresCompanyWhenDisplayNameIsEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso", email: "alice@example.com");
        var right = CreateContact("b1", string.Empty, organization: "Fabrikam", email: "alice@example.com");

        Assert.False(ItemMatcher.IsMatch(left, right));
    }

    [Fact]
    public void IsMatch_RequiresEmailOrPhoneOverlap()
    {
        var left = CreateContact("a1", "Alice Smith", organization: "Contoso", email: "alice@example.com");
        var right = CreateContact("b1", "Alice Smith", organization: "Contoso", email: "other@example.com");

        Assert.False(ItemMatcher.IsMatch(left, right));
    }

    [Fact]
    public void IsMatch_MatchesEventsByNormalizedTitleAndStartInstant()
    {
        var left = CreateEvent("a1", "  Design Review ", new DateTimeOffset(2026, 5, 14, 15, 30, 0, TimeSpan.Zero));
        var right = CreateEvent("b1", "Design Review", new DateTimeOffset(2026, 5, 14, 17, 30, 0, TimeSpan.FromHours(2)));

        Assert.True(ItemMatcher.IsMatch(left, right));
    }

    [Fact]
    public void IsMatch_IgnoresSubsecondProviderDifferencesInEventStart()
    {
        var left = CreateEvent("a1", "Celebration", new DateTimeOffset(2026, 4, 18, 13, 0, 0, 555, TimeSpan.Zero));
        var right = CreateEvent("b1", "Celebration", new DateTimeOffset(2026, 4, 18, 15, 0, 0, TimeSpan.FromHours(2)));

        Assert.True(ItemMatcher.IsMatch(left, right));
    }

    [Fact]
    public void BuildDuplicateCandidateMap_MatchesUniqueEventsByTitleAndStart()
    {
        var source = CreateEvent("a1", "Design Review", new DateTimeOffset(2026, 5, 14, 15, 30, 0, TimeSpan.Zero));
        var target = CreateEvent("b1", " design review ", new DateTimeOffset(2026, 5, 14, 17, 30, 0, TimeSpan.FromHours(2)));

        var candidates = ItemMatcher.BuildDuplicateCandidateMap([source], [target]);

        Assert.Same(target, Assert.Single(candidates["a1"]));
    }

    [Fact]
    public void BuildDuplicateCandidateMap_DoesNotAutoLinkAmbiguousEvents()
    {
        var source = CreateEvent("a1", "Design Review", new DateTimeOffset(2026, 5, 14, 15, 30, 0, TimeSpan.Zero));
        var target1 = CreateEvent("b1", "Design Review", source.From);
        var target2 = CreateEvent("b2", "Design Review", source.From);

        var candidates = ItemMatcher.BuildDuplicateCandidateMap([source], [target1, target2]);

        Assert.Empty(candidates["a1"]);
    }

    // ── HasNameMatch ────────────────────────────────────────────────────

    [Fact]
    public void HasNameMatch_ReturnsTrueWhenDisplayNamesMatch()
    {
        var left = CreateContact("a1", "Alice Smith");
        var right = CreateContact("b1", " Alice Smith ");  // extra whitespace

        Assert.True(ItemMatcher.HasNameMatch(left, right));
    }

    [Fact]
    public void HasNameMatch_ReturnsFalseWhenDisplayNamesDiffer()
    {
        var left = CreateContact("a1", "Alice");
        var right = CreateContact("b1", "Bob");

        Assert.False(ItemMatcher.HasNameMatch(left, right));
    }

    [Fact]
    public void HasNameMatch_FallsBackToOrganisationWhenDisplayNameEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso");
        var right = CreateContact("b1", string.Empty, organization: "Contoso");

        Assert.True(ItemMatcher.HasNameMatch(left, right));
    }

    [Fact]
    public void HasNameMatch_ReturnsFalseWhenOrganisationsDifferAndDisplayNameEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso");
        var right = CreateContact("b1", string.Empty, organization: "Fabrikam");

        Assert.False(ItemMatcher.HasNameMatch(left, right));
    }

    // ── HasDetailMatch ──────────────────────────────────────────────────

    [Fact]
    public void HasDetailMatch_ReturnsFalseWhenNeitherSideHasIdentifiers()
    {
        var left = CreateContact("a1", "Alice");
        var right = CreateContact("b1", "Alice");

        Assert.False(ItemMatcher.HasDetailMatch(left, right));
    }

    [Fact]
    public void HasDetailMatch_ReturnsFalseWhenNoOverlap()
    {
        var left = CreateContact("a1", "Alice", email: "alice@example.com");
        var right = CreateContact("b1", "Alice", email: "other@example.com");

        Assert.False(ItemMatcher.HasDetailMatch(left, right));
    }

    [Fact]
    public void HasDetailMatch_ReturnsTrueWhenEmailOverlaps()
    {
        var left = CreateContact("a1", "Alice", email: "Alice@Example.com");
        var right = CreateContact("b1", "Alice", email: " alice@example.com ");

        Assert.True(ItemMatcher.HasDetailMatch(left, right));
    }

    [Fact]
    public void HasDetailMatch_ReturnsTrueWhenPhoneOverlaps()
    {
        var left = CreateContact("a1", "Alice", phone: "+1 (555) 123-4567");
        var right = CreateContact("b1", "Alice", phone: "15551234567");

        Assert.True(ItemMatcher.HasDetailMatch(left, right));
    }

    private static CanonicalContact CreateContact(
        string id,
        string displayName,
        string? organization = null,
        string? email = null,
        string? phone = null) =>
        new()
        {
            DisplayName = displayName,
            Organization = organization,
            Emails = email is null ? [] : [new ContactEmail { Address = email }],
            Phones = phone is null ? [] : [new ContactPhone { Number = phone }],

            Provenance =
            {
                ProviderId = id,
            },
        };

    private static CanonicalEvent CreateEvent(string id, string title, DateTimeOffset from) => new()
    {
        Title = title,
        From = from,
        To = from.AddHours(1),
        Provenance =
        {
            ProviderId = id,
        },
    };
}
