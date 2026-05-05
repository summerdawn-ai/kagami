namespace Summerdawn.Kagami.Models;

public sealed class ItemProvenance
{
    /// <summary>
    /// Provider-assigned identifier on the source side.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Provider-assigned version/etag.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Content hash for change detection.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>Last modified time (UTC).</summary>
    public DateTimeOffset? LastModified { get; set; }
}
