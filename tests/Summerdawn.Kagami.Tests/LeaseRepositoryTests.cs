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
    public async Task Lease_PreventsOverlap()
    {
        Assert.True(await repo.TryAcquireAsync("job-1"));
        Assert.False(await repo.TryAcquireAsync("job-1"));
    }

    [Fact]
    public async Task ReleasedLease_CanBeReacquired()
    {
        await repo.TryAcquireAsync("job-1");
        await repo.ReleaseAsync("job-1");

        Assert.True(await repo.TryAcquireAsync("job-1"));
    }

    [Fact]
    public async Task LockRow_PersistsAfterRelease_SoJobAppearsInList()
    {
        await repo.TryAcquireAsync("contacts:A:B");
        await repo.ReleaseAsync("contacts:A:B");

        var rows = await repo.ListAllAsync();

        Assert.Contains(rows, r => r.JobKey == "contacts:A:B" && !r.Locked);
    }

    [Fact]
    public async Task ForceReleaseAsync_ClearsSpecificLock()
    {
        await repo.TryAcquireAsync("job-1");
        await repo.TryAcquireAsync("job-2");

        int cleared = await repo.ForceReleaseAsync("job-1");

        Assert.Equal(1, cleared);
        Assert.False(await repo.IsLockedAsync("job-1"));
        Assert.True(await repo.IsLockedAsync("job-2"));
    }

    [Fact]
    public async Task ForceReleaseAllAsync_ClearsAllLocks()
    {
        await repo.TryAcquireAsync("job-1");
        await repo.TryAcquireAsync("job-2");

        int cleared = await repo.ForceReleaseAllAsync();

        Assert.Equal(2, cleared);
        Assert.False(await repo.IsLockedAsync("job-1"));
        Assert.False(await repo.IsLockedAsync("job-2"));
    }

    [Fact]
    public async Task ListAllAsync_ReturnsAllKnownJobs()
    {
        await repo.TryAcquireAsync("contacts:A:B");
        await repo.TryAcquireAsync("contacts:C:D");
        await repo.ReleaseAsync("contacts:A:B");

        var rows = await repo.ListAllAsync();

        Assert.Contains(rows, r => r.JobKey == "contacts:A:B" && !r.Locked);
        Assert.Contains(rows, r => r.JobKey == "contacts:C:D" && r.Locked);
    }
}
