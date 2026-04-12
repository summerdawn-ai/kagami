namespace Summerdawn.Kagami.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestSupport;

public sealed class LinkStateRepositoryTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly LinkStateRepository repo;

    public LinkStateRepositoryTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        repo = new LinkStateRepository(db);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task LinkStateIsPartitionedByJobKey()
    {
        await repo.UpsertAsync(new LinkStateRow { JobKey = "job-1", EntityType = "contact", SideAId = "a1" });
        await repo.UpsertAsync(new LinkStateRow { JobKey = "job-2", EntityType = "contact", SideAId = "a1" });

        var job1Rows = await repo.GetByJobAsync("job-1");
        var job2Rows = await repo.GetByJobAsync("job-2");

        Assert.Single(job1Rows);
        Assert.Single(job2Rows);
    }

    [Fact]
    public async Task DeleteByJobRemovesOnlyTargetRows()
    {
        await repo.UpsertAsync(new LinkStateRow { JobKey = "job-1", EntityType = "contact", SideAId = "a1" });
        await repo.UpsertAsync(new LinkStateRow { JobKey = "job-2", EntityType = "contact", SideAId = "a2" });

        await repo.DeleteByJobAsync("job-1");

        Assert.Empty(await repo.GetByJobAsync("job-1"));
        Assert.Single(await repo.GetByJobAsync("job-2"));
    }

    [Fact]
    public async Task LinkStateDoesNotStoreMutableJobPolicy()
    {
        await repo.UpsertAsync(new LinkStateRow
        {
            JobKey = "job-1",
            EntityType = "calendar-event",
            SideAId = "a1",
            SideBId = "b1",
            SideAVersion = "v1",
            SideBVersion = "v1",
            OriginSide = "A",
        });

        var row = await repo.GetBySideAIdAsync("job-1", "a1");

        Assert.NotNull(row);
        Assert.Equal("v1", row.SideAVersion);
        Assert.Equal("v1", row.SideBVersion);
    }
}
