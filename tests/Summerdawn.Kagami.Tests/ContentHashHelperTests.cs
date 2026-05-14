using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class ContentHashHelperTests
{
    [Fact]
    public void ComputeContentHash_ReturnsStableHashForCanonicalEvent()
    {
        var first = CreateEvent("google-1", "Google");
        var second = CreateEvent("microsoft-1", "Microsoft");

        string firstHash = ContentHashHelper.ComputeContentHash(first);
        string secondHash = ContentHashHelper.ComputeContentHash(second);

        Assert.NotEmpty(firstHash);
        Assert.Equal(firstHash, secondHash);
    }

    private static CanonicalEvent CreateEvent(string providerId, string endpointName) => new()
    {
        Title = "Design Review",
        Description = "Agenda",
        From = new DateTimeOffset(2026, 05, 14, 15, 30, 00, TimeSpan.Zero),
        To = new DateTimeOffset(2026, 05, 14, 16, 00, 00, TimeSpan.Zero),
        Location = "Zoom",
        ICalUid = "design-review@example.test",
        Provenance =
        {
            ProviderId = providerId,
            EndpointName = endpointName,
            Version = "v1",
        }
    };
}
