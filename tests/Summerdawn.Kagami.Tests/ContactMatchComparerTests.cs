using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class ContactMatchComparerTests
{
    private readonly ContactMatchComparer comparer = new();

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

        Assert.True(comparer.IsMatch(left, right));
    }

    [Fact]
    public void IsMatch_RequiresCompanyWhenDisplayNameIsEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso", email: "alice@example.com");
        var right = CreateContact("b1", string.Empty, organization: "Fabrikam", email: "alice@example.com");

        Assert.False(comparer.IsMatch(left, right));
    }

    [Fact]
    public void IsMatch_RequiresEmailOrPhoneOverlap()
    {
        var left = CreateContact("a1", "Alice Smith", organization: "Contoso", email: "alice@example.com");
        var right = CreateContact("b1", "Alice Smith", organization: "Contoso", email: "other@example.com");

        Assert.False(comparer.IsMatch(left, right));
    }

    // ── HasNameMatch ────────────────────────────────────────────────────

    [Fact]
    public void HasNameMatch_ReturnsTrueWhenDisplayNamesMatch()
    {
        var left = CreateContact("a1", "Alice Smith");
        var right = CreateContact("b1", " Alice Smith ");  // extra whitespace

        Assert.True(comparer.HasNameMatch(left, right));
    }

    [Fact]
    public void HasNameMatch_ReturnsFalseWhenDisplayNamesDiffer()
    {
        var left = CreateContact("a1", "Alice");
        var right = CreateContact("b1", "Bob");

        Assert.False(comparer.HasNameMatch(left, right));
    }

    [Fact]
    public void HasNameMatch_FallsBackToOrganisationWhenDisplayNameEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso");
        var right = CreateContact("b1", string.Empty, organization: "Contoso");

        Assert.True(comparer.HasNameMatch(left, right));
    }

    [Fact]
    public void HasNameMatch_ReturnsFalseWhenOrganisationsDifferAndDisplayNameEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso");
        var right = CreateContact("b1", string.Empty, organization: "Fabrikam");

        Assert.False(comparer.HasNameMatch(left, right));
    }

    // ── HasDetailMatch ──────────────────────────────────────────────────

    [Fact]
    public void HasDetailMatch_ReturnsFalseWhenNeitherSideHasIdentifiers()
    {
        var left = CreateContact("a1", "Alice");
        var right = CreateContact("b1", "Alice");

        Assert.False(comparer.HasDetailMatch(left, right));
    }

    [Fact]
    public void HasDetailMatch_ReturnsFalseWhenNoOverlap()
    {
        var left = CreateContact("a1", "Alice", email: "alice@example.com");
        var right = CreateContact("b1", "Alice", email: "other@example.com");

        Assert.False(comparer.HasDetailMatch(left, right));
    }

    [Fact]
    public void HasDetailMatch_ReturnsTrueWhenEmailOverlaps()
    {
        var left = CreateContact("a1", "Alice", email: "Alice@Example.com");
        var right = CreateContact("b1", "Alice", email: " alice@example.com ");

        Assert.True(comparer.HasDetailMatch(left, right));
    }

    [Fact]
    public void HasDetailMatch_ReturnsTrueWhenPhoneOverlaps()
    {
        var left = CreateContact("a1", "Alice", phone: "+1 (555) 123-4567");
        var right = CreateContact("b1", "Alice", phone: "15551234567");

        Assert.True(comparer.HasDetailMatch(left, right));
    }

    private static CanonicalItem CreateContact(
        string id,
        string displayName,
        string? organization = null,
        string? email = null,
        string? phone = null) =>
        new()
        {
            EntityType = EntityType.Contact,
            SourceId = id,
            Payload = new CanonicalContact
            {
                DisplayName = displayName,
                Organization = organization,
                Emails = email is null ? [] : [new ContactEmail { Address = email }],
                Phones = phone is null ? [] : [new ContactPhone { Number = phone }],
            },
        };
}
