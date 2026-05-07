using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Defines a provider-neutral abstraction over a single endpoint.
/// </summary>
public interface IConnector<TItem> where TItem : CanonicalItem
{
    /// <summary>
    /// Gets the connector's capability declaration.
    /// </summary>
    public ConnectorCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the name of the endpoint this connector connects to.
    /// </summary>
    public string EndpointName { get; }

    /// <summary>
    /// Authenticates and initializes the connector.
    /// </summary>
    public Task AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the connector's current item set, starting from an initial/full read when <paramref name="cursor"/> is <c>null</c>
    /// or from an incremental read when a prior cursor is supplied.
    /// </summary>
    /// <param name="cursor">The cursor returned by a previous call, or <c>null</c> to start from the connector's initial/full read path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task<ItemSet<TItem>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single item by its provider ID, or returns <c>null</c> when the item does not exist.
    /// </summary>
    public Task<TItem?> GetItemAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new item on the remote endpoint and returns the created item with its assigned ID.
    /// </summary>
    public Task<TItem> CreateItemAsync(TItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing item on the remote endpoint and returns the updated item.
    /// </summary>
    public Task<TItem> UpdateItemAsync(TItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an item by its provider ID.
    /// </summary>
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default);
}
