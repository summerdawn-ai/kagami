using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class EventFilterTests
{
    [Theory]
    [InlineData("startswith(title,'Tea')", "Team Sync", true)]
    [InlineData("endswith(title,'Sync')", "Team Sync", true)]
    [InlineData("contains(title,'eam')", "Team Sync", true)]
    [InlineData("title eq 'Team Sync'", "Team Sync", true)]
    [InlineData("title eq 'Other'", "Team Sync", false)]
    public void Parse_KnownPattern_MatchesAsExpected(string expression, string title, bool expected)
    {
        var filter = EventFilter.Parse(expression);

        Assert.NotNull(filter);
        Assert.Equal(expected, filter!.Matches(new CanonicalEvent { Title = title }));
        Assert.Equal(expression, filter.Scope);
    }

    [Fact]
    public void Parse_NullExpression_ReturnsNull()
    {
        Assert.Null(EventFilter.Parse(null));
        Assert.Null(EventFilter.Parse("  "));
    }

    [Fact]
    public void Parse_InvalidExpression_Throws()
    {
        Assert.Throws<ArgumentException>(() => EventFilter.Parse("startswith(name,'x')"));
    }

    [Fact]
    public void Apply_FiltersMatchingEvents()
    {
        var filter = EventFilter.Parse("contains(title,'Sync')");
        var items = new[]
        {
            new CanonicalEvent { Title = "Team Sync" },
            new CanonicalEvent { Title = "Planning" },
        };

        var result = filter!.Apply(items);

        var matched = Assert.Single(result);
        Assert.Equal("Team Sync", matched.Title);
    }
}
