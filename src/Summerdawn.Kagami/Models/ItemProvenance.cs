namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provider-assigned identity and change-tracking metadata attached to a canonical item.
/// </summary>
public sealed class ItemProvenance
{
    /// <summary>
    /// Provider-assigned identifier on the source side.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Provider-assigned version or ETag, used for optimistic change detection.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Content hash for change detection when version/ETag is unavailable.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// When the item was last modified on the provider side (UTC).
    /// </summary>
    public DateTimeOffset? LastModified { get; set; }
}
