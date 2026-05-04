using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests.TestDoubles;
/// <summary>
/// A test connector that delegates to a <see cref="FakeConnector"/> for all operations
/// except <see cref="CreateItemAsync"/>, which throws <see cref="InvalidOperationException"/>
/// for the item whose <see cref="CanonicalItem.SourceId"/> matches <paramref name="throwForSourceId"/>.
/// </summary>
public sealed class FailingCreateConnector(string throwForSourceId) : IConnector
{
    private readonly FakeConnector inner = new();

    public IReadOnlyList<CanonicalItem> Items => inner.Items;

    public ConnectorCapabilities Capabilities => inner.Capabilities;

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        inner.GetInitialPageAsync(cancellationToken);

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default) =>
        inner.GetIncrementalPageAsync(cursor, cancellationToken);

    public Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
        inner.GetItemAsync(id, cancellationToken);

    public Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        if (item.SourceId == throwForSourceId)
        {
            throw new InvalidOperationException($"Simulated failure creating item {item.SourceId}");
        }

        return inner.CreateItemAsync(item, cancellationToken);
    }

    public Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default) =>
        inner.UpdateItemAsync(item, cancellationToken);

    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) =>
        inner.DeleteItemAsync(id, cancellationToken);
}
