using System.Net;

using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests.TestDoubles;

/// <summary>
/// A test connector that delegates to a <see cref="FakeConnector"/> for all operations
/// except <see cref="CreateItemAsync"/>, which throws an <see cref="HttpRequestException"/>
/// with the given <paramref name="statusCode"/> for every item.
/// </summary>
public sealed class TransientlyFailingConnector(HttpStatusCode statusCode = HttpStatusCode.TooManyRequests) : IConnector<CanonicalContact>
{
    private readonly FakeConnector inner = new();

    public IReadOnlyList<CanonicalContact> Items => inner.Items;

    public ConnectorCapabilities Capabilities => inner.Capabilities;

    public string EndpointName => "failing-transiently-test";

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ItemSet<CanonicalContact>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default) =>
        inner.GetCursorItemsAsync(cursor, cancellationToken);

    public Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
        inner.GetItemAsync(id, cancellationToken);

    public Task<CanonicalContact> CreateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException($"Simulated transient failure ({statusCode})", null, statusCode);

    public Task<CanonicalContact> UpdateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default) =>
        inner.UpdateItemAsync(item, cancellationToken);

    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default) =>
        inner.DeleteItemAsync(id, cancellationToken);
}
