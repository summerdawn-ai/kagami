
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;
public sealed class ContactMatchComparerTests
{
    private readonly ContactMatchComparer comparer = new();

    [Fact]
    public void IsMatchNormalizesEmailAndPhone()
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
    public void IsMatchRequiresCompanyWhenDisplayNameIsEmpty()
    {
        var left = CreateContact("a1", string.Empty, organization: "Contoso", email: "alice@example.com");
        var right = CreateContact("b1", string.Empty, organization: "Fabrikam", email: "alice@example.com");

        Assert.False(comparer.IsMatch(left, right));
    }

    [Fact]
    public void IsMatchRequiresEmailOrPhoneOverlap()
    {
        var left = CreateContact("a1", "Alice Smith", organization: "Contoso", email: "alice@example.com");
        var right = CreateContact("b1", "Alice Smith", organization: "Contoso", email: "other@example.com");

        Assert.False(comparer.IsMatch(left, right));
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
