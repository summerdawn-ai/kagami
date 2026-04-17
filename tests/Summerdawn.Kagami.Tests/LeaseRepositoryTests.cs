
using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

public sealed class LeaseRepositoryTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly LeaseRepository repo;

    public LeaseRepositoryTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        repo = new LeaseRepository(db);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task LeasePreventsOverlap()
    {
        Assert.True(await repo.TryAcquireAsync("job-1", "holder-a", TimeSpan.FromMinutes(5)));
        Assert.False(await repo.TryAcquireAsync("job-1", "holder-b", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ReleasedLeaseCanBeReacquired()
    {
        await repo.TryAcquireAsync("job-1", "holder-a", TimeSpan.FromMinutes(5));
        await repo.ReleaseAsync("job-1", "holder-a");

        Assert.True(await repo.TryAcquireAsync("job-1", "holder-b", TimeSpan.FromMinutes(5)));
    }
}
