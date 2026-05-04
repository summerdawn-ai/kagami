namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provider-specific and sync-related metadata for a canonical item.
/// </summary>
public sealed class ItemProvenance
{
    /// <summary>Provider-assigned identifier on the source side.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Provider-assigned version/etag.</summary>
    public string? Version { get; set; }

    /// <summary>Content hash for change detection.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Whether this item has been deleted on the source side.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Metadata bag for provider-specific or extensibility data.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>Last modified time (UTC) as reported by the provider.</summary>
    public DateTimeOffset? LastModified { get; set; }
}
