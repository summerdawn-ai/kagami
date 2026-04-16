namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Represents a correspondence/link record between two provider items.
/// Stores historical facts; job policy is NOT stored here.
/// </summary>
public sealed class LinkStateRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>Stable configured job key (partition key).</summary>
    public string JobKey { get; set; } = string.Empty;

    /// <summary>Entity type (e.g., "calendar-event", "contact").</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Provider ID on the source side.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Provider ID on the destination side (null before first successful sync).</summary>
    public string? DestinationId { get; set; }

    /// <summary>Version/etag on the source side.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>Version/etag on the destination side.</summary>
    public string? DestinationVersion { get; set; }

    /// <summary>Content hash on the source side.</summary>
    public string? SourceHash { get; set; }

    /// <summary>Content hash on the destination side.</summary>
    public string? DestinationHash { get; set; }

    /// <summary>Whether the source side has deleted this item.</summary>
    public bool SourceDeleted { get; set; }

    /// <summary>Whether the destination side has deleted this item.</summary>
    public bool DestinationDeleted { get; set; }

    /// <summary>Last time the source side was observed.</summary>
    public DateTimeOffset? SourceLastSeen { get; set; }

    /// <summary>Last time the destination side was observed.</summary>
    public DateTimeOffset? DestinationLastSeen { get; set; }

    /// <summary>Which side originally created the item ("Source" or "Destination").</summary>
    public string? OriginSide { get; set; }

    /// <summary>When the item was last successfully synchronized.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Result of the last sync operation.</summary>
    public string? LastSyncResult { get; set; }

    /// <summary>Current conflict state, if any.</summary>
    public string? ConflictState { get; set; }
}
