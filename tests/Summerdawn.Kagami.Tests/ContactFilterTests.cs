using Summerdawn.Kagami.Models;

using ContactFilter = Summerdawn.Kagami.Models.ContactFilter;

namespace Summerdawn.Kagami.Tests;

public sealed class ContactFilterTests
{
    // ── Parse returns null for empty expression ───────────────────────────

    [Fact]
    public void ParseNull_ReturnsNull()
    {
        Assert.Null(ContactFilter.Parse(null));
    }

    [Fact]
    public void ParseEmptyString_ReturnsNull()
    {
        Assert.Null(ContactFilter.Parse(string.Empty));
    }

    [Fact]
    public void ParseWhitespace_ReturnsNull()
    {
        Assert.Null(ContactFilter.Parse("   "));
    }

    // ── startswith ────────────────────────────────────────────────────────

    [Fact]
    public void StartsWith_MatchesPrefix()
    {
        var f = ContactFilter.Parse("startswith(name,'Al')");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("Alice")));
        Assert.False(f.Matches(MakeContact("Bob")));
    }

    [Fact]
    public void StartsWith_IsCaseInsensitive()
    {
        var f = ContactFilter.Parse("startswith(name,'al')");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("Alice")));
    }

    [Fact]
    public void StartsWith_IgnoresExtraWhitespace()
    {
        var f = ContactFilter.Parse("startswith( name , 'A' )");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("Alice")));
    }

    // ── endswith ──────────────────────────────────────────────────────────

    [Fact]
    public void EndsWith_MatchesSuffix()
    {
        var f = ContactFilter.Parse("endswith(name,'son')");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("Anderson")));
        Assert.False(f.Matches(MakeContact("Smith")));
    }

    // ── contains ──────────────────────────────────────────────────────────

    [Fact]
    public void Contains_MatchesSubstring()
    {
        var f = ContactFilter.Parse("contains(name,'Donald')");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("McDonald")));
        Assert.False(f.Matches(MakeContact("Smith")));
    }

    // ── eq ────────────────────────────────────────────────────────────────

    [Fact]
    public void Eq_MatchesExactName()
    {
        var f = ContactFilter.Parse("name eq 'Alice'");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("Alice")));
        Assert.False(f.Matches(MakeContact("Alice Smith")));
    }

    [Fact]
    public void Name_FallsBackToOrganization()
    {
        var f = ContactFilter.Parse("contains(name,'Contoso')");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact(displayName: string.Empty, organization: "Contoso Ltd")));
        Assert.False(f.Matches(MakeContact(displayName: string.Empty, organization: "Fabrikam")));
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_FiltersCollection()
    {
        var f = ContactFilter.Parse("startswith(name,'A')")!;
        var contacts = new[]
        {
            MakeContact("Alice"),
            MakeContact("Bob"),
            MakeContact("Andrew"),
        };
        var result = f.Apply(contacts);
        Assert.Equal(2, result.Count);
        Assert.All(result, contact =>
        {
            Assert.StartsWith("A", contact.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── Escaped single quotes (OData '' → ') ─────────────────────────────

    [Fact]
    public void StartsWith_HandlesEscapedQuoteInValue()
    {
        // OData encoding: '' represents a literal single quote
        var f = ContactFilter.Parse("startswith(name,'O''Brien')");
        Assert.NotNull(f);
        Assert.True(f.Matches(MakeContact("O'Brien")));
        Assert.False(f.Matches(MakeContact("O Brien")));
    }

    // ── Unknown expression throws ─────────────────────────────────────────

    [Fact]
    public void UnrecognizedExpression_Throws()
    {
        Assert.Throws<ArgumentException>(() => ContactFilter.Parse("lt(name,'A')"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static CanonicalContact MakeContact(string displayName, string? organization = null) => new()
    {
        DisplayName = displayName,
        Organization = organization,

        Provenance =
        {
            ProviderId = Guid.NewGuid().ToString("N"),
        }
    };
}
