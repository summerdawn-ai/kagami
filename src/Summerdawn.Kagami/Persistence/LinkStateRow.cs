namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Represents a link record that tracks the correspondence between a source item and its synced
/// destination counterpart. Stores observed facts only; job policy is not stored here.
/// </summary>
public sealed class LinkStateRow
{
    /// <summary>
    /// Gets or sets the auto-increment primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the partition key that groups link rows sharing the same endpoint pair.
    /// </summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider-assigned ID of the source item.
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider-assigned ID of the destination item, or <c>null</c> before the first successful sync.
    /// </summary>
    public string? DestinationId { get; set; }

    /// <summary>
    /// Gets or sets the source version or ETag at the time of the last sync.
    /// </summary>
    public string? SourceVersion { get; set; }

    /// <summary>
    /// Gets or sets the destination version or ETag at the time of the last sync.
    /// </summary>
    public string? DestinationVersion { get; set; }

    /// <summary>
    /// Gets or sets the source content hash at the time of the last sync.
    /// </summary>
    public string? SourceHash { get; set; }

    /// <summary>
    /// Gets or sets the destination content hash at the time of the last sync.
    /// </summary>
    public string? DestinationHash { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source item has been deleted.
    /// </summary>
    public bool SourceDeleted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the destination item has been deleted.
    /// </summary>
    public bool DestinationDeleted { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the source item was last observed.
    /// </summary>
    public DateTimeOffset? SourceLastSeen { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the destination item was last observed.
    /// </summary>
    public DateTimeOffset? DestinationLastSeen { get; set; }

    /// <summary>
    /// Gets or sets which side originally created the item (<c>"Source"</c> or <c>"Destination"</c>).
    /// </summary>
    public string? OriginSide { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful sync.
    /// </summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>
    /// Gets or sets the result of the last sync operation (e.g. <c>"created"</c>, <c>"updated"</c>, <c>"unchanged"</c>).
    /// </summary>
    public string? LastSyncResult { get; set; }

    /// <summary>
    /// Gets or sets the current conflict state, if any.
    /// </summary>
    public string? ConflictState { get; set; }
}
