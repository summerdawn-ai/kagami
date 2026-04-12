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

    /// <summary>Provider ID on side A.</summary>
    public string SideAId { get; set; } = string.Empty;

    /// <summary>Provider ID on side B (null before first successful sync).</summary>
    public string? SideBId { get; set; }

    /// <summary>Version/etag on side A.</summary>
    public string? SideAVersion { get; set; }

    /// <summary>Version/etag on side B.</summary>
    public string? SideBVersion { get; set; }

    /// <summary>Content hash on side A.</summary>
    public string? SideAHash { get; set; }

    /// <summary>Content hash on side B.</summary>
    public string? SideBHash { get; set; }

    /// <summary>Whether side A has deleted this item.</summary>
    public bool SideADeleted { get; set; }

    /// <summary>Whether side B has deleted this item.</summary>
    public bool SideBDeleted { get; set; }

    /// <summary>Last time side A was observed.</summary>
    public DateTimeOffset? SideALastSeen { get; set; }

    /// <summary>Last time side B was observed.</summary>
    public DateTimeOffset? SideBLastSeen { get; set; }

    /// <summary>Which side originally created the item ("A" or "B").</summary>
    public string? OriginSide { get; set; }

    /// <summary>When the item was last successfully synchronized.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Result of the last sync operation.</summary>
    public string? LastSyncResult { get; set; }

    /// <summary>Current conflict state, if any.</summary>
    public string? ConflictState { get; set; }
}
