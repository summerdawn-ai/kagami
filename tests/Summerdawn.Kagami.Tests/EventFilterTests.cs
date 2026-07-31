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

    [Fact]
    public void Parse_AndRequiresEveryClauseToMatch()
    {
        const string expression = "contains(title,'Sync') and start gt '2026-01-01T00:00:00Z' and end lt '2026-02-01T00:00:00Z'";
        var filter = EventFilter.Parse(expression);

        Assert.NotNull(filter);
        Assert.Equal(expression, filter!.Scope);
        Assert.True(filter.Matches(new CanonicalEvent
        {
            Title = "Team Sync",
            From = new DateTimeOffset(2026, 1, 10, 9, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero),
        }));
        Assert.False(filter.Matches(new CanonicalEvent
        {
            Title = "Team Sync",
            From = new DateTimeOffset(2025, 12, 10, 9, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2025, 12, 10, 10, 0, 0, TimeSpan.Zero),
        }));
    }

    [Fact]
    public void Parse_AndInsideQuotedValueIsNotASeparator()
    {
        var filter = EventFilter.Parse("contains(title,'Research and Planning') and start gt '2026-01-01T00:00:00Z'");

        Assert.NotNull(filter);
        Assert.True(filter!.Matches(new CanonicalEvent
        {
            Title = "Research and Planning",
            From = new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero),
        }));
    }

    [Fact]
    public void Parse_DateComparisonsUseStrictBoundaries()
    {
        var filter = EventFilter.Parse("start gt '2026-01-01T00:00:00Z' and end lt '2026-01-02T00:00:00Z'");

        Assert.NotNull(filter);
        Assert.False(filter!.Matches(new CanonicalEvent
        {
            From = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        }));
    }
}
