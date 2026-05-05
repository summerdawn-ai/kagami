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
    /// Authenticates and initializes the connector.
    /// </summary>
    public Task AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches all items as the initial full sync page.
    /// </summary>
    public Task<IncrementalPage<TItem>> GetInitialPageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches incremental changes since the cursor returned by a previous call.
    /// </summary>
    /// <param name="cursor">The cursor/sync token returned by a previous call.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task<IncrementalPage<TItem>> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default);

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
