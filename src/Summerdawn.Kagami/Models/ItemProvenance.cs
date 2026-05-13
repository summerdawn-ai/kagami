namespace Summerdawn.Kagami.Models;

/// <summary>
/// Represents provider-assigned identity and change-tracking metadata for a canonical item.
/// </summary>
public sealed class ItemProvenance
{
    /// <summary>
    /// Gets or sets the provider-assigned item identifier.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the endpoint that owns this item, used for logging.
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Gets or sets the provider-assigned version or ETag, used for optimistic change detection.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 content hash, used for change detection when a version/ETag is unavailable.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last modification on the provider side.
    /// </summary>
    public DateTimeOffset? LastModified { get; set; }
}
