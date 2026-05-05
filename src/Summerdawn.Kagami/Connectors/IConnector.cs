using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Provider-neutral connector abstraction for a single endpoint.
/// </summary>
public interface IConnector<TItem> where TItem : CanonicalItem
{
    /// <summary>Gets the connector's capability declaration.</summary>
    public ConnectorCapabilities Capabilities { get; }

    /// <summary>
    /// Performs any required authentication / initialization.
    /// </summary>
    public Task AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates all items as an initial full sync page.
    /// </summary>
    public Task<IncrementalPage<TItem>> GetInitialPageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates incremental changes since the last cursor.
    /// </summary>
    /// <param name="cursor">The cursor/sync token returned by a previous call.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task<IncrementalPage<TItem>> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single item by its provider ID.
    /// </summary>
    public Task<TItem?> GetItemAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new item on the remote and returns the created item (with assigned ID).
    /// </summary>
    public Task<TItem> CreateItemAsync(TItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing item.
    /// </summary>
    public Task<TItem> UpdateItemAsync(TItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an item by its provider ID.
    /// </summary>
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default);
}
