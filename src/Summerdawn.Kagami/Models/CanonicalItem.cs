namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provider-neutral wrapper around a synchronized item.
/// </summary>
public sealed class CanonicalItem
{
    /// <summary>
    /// Entity type identifier (e.g., "calendar-event", "contact").
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// The typed payload — either a <see cref="CanonicalCalendarEvent"/> or <see cref="CanonicalContact"/>.
    /// </summary>
    public object? Payload { get; set; }

    /// <summary>
    /// Provider-assigned identifier on the source side.
    /// </summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>
    /// Provider-assigned version/etag.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Content hash for change detection.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Whether this item has been deleted on the source side.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Metadata bag for provider-specific or extensibility data.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}
