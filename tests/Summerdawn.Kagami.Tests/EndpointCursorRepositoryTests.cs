
using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Tests.TestSupport;

namespace Summerdawn.Kagami.Tests;
public sealed class EndpointCursorRepositoryTests : IDisposable
{
    private readonly TestDatabasePath databasePath = new();
    private readonly StateDatabase db;
    private readonly EndpointCursorRepository repository;

    public EndpointCursorRepositoryTests()
    {
        db = new StateDatabase(databasePath.Path, NullLogger<StateDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        repository = new EndpointCursorRepository(db);
    }

    public void Dispose() => databasePath.Dispose();

    [Fact]
    public async Task CursorStateIsScopedByJobAndStoresScope()
    {
        await repository.SetCursorAsync("job-1", "endpointA", "startswith(name,'A')", "cursor-a");
        await repository.SetCursorAsync("job-2", "endpointA", string.Empty, "cursor-b");

        var job1 = await repository.GetCursorAsync("job-1", "endpointA");
        var job2 = await repository.GetCursorAsync("job-2", "endpointA");
        var missing = await repository.GetCursorAsync("job-3", "endpointA");

        Assert.NotNull(job1);
        Assert.Equal("cursor-a", job1!.Cursor);
        Assert.Equal("startswith(name,'A')", job1.Scope);
        Assert.NotNull(job2);
        Assert.Equal("cursor-b", job2!.Cursor);
        Assert.Equal(string.Empty, job2.Scope);
        Assert.Null(missing);
    }
}
