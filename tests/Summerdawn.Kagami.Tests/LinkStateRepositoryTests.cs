using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;

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
    public async Task LinkState_IsPartitionedByPartitionKey()
    {
        await repo.UpsertAsync(new LinkStateRow { PartitionKey = "contact:endpointA:endpointB", SourceId = "a1" });
        await repo.UpsertAsync(new LinkStateRow { PartitionKey = "contact:endpointC:endpointD", SourceId = "a1" });

        var partition1Rows = await repo.GetByPartitionAsync("contact:endpointA:endpointB");
        var partition2Rows = await repo.GetByPartitionAsync("contact:endpointC:endpointD");

        Assert.Single(partition1Rows);
        Assert.Single(partition2Rows);
    }

    [Fact]
    public async Task DeleteByPartition_RemovesOnlyTargetRows()
    {
        await repo.UpsertAsync(new LinkStateRow { PartitionKey = "contact:endpointA:endpointB", SourceId = "a1" });
        await repo.UpsertAsync(new LinkStateRow { PartitionKey = "contact:endpointC:endpointD", SourceId = "a2" });

        await repo.DeleteByPartitionAsync("contact:endpointA:endpointB");

        Assert.Empty(await repo.GetByPartitionAsync("contact:endpointA:endpointB"));
        Assert.Single(await repo.GetByPartitionAsync("contact:endpointC:endpointD"));
    }

    [Fact]
    public async Task LinkState_DoesNotStoreMutableJobPolicy()
    {
        await repo.UpsertAsync(new LinkStateRow
        {
            PartitionKey = "contact:endpointA:endpointB",
            SourceId = "a1",
            DestinationId = "b1",
            SourceVersion = "v1",
            DestinationVersion = "v1",
            OriginSide = "Source",
        });

        var row = await repo.GetBySourceIdAsync("contact:endpointA:endpointB", "a1");

        Assert.NotNull(row);
        Assert.Equal("v1", row.SourceVersion);
        Assert.Equal("v1", row.DestinationVersion);
    }

    [Fact]
    public async Task LinkState_CliAndConfiguredJobSharePartition_ForSameEndpointPair()
    {
        // Simulates a configured job writing a link row under the shared partition key.
        const string partitionKey = "contact:google:microsoft";
        await repo.UpsertAsync(new LinkStateRow { PartitionKey = partitionKey, SourceId = "a1", DestinationId = "b1" });

        // A CLI sync run for the same endpoint pair should see the same row.
        var rows = await repo.GetByPartitionAsync(partitionKey);

        Assert.Single(rows);
        Assert.Equal("a1", rows[0].SourceId);
        Assert.Equal("b1", rows[0].DestinationId);
    }
}
