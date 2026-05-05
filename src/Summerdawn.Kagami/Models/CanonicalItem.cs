using System.Text.Json.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provider-neutral wrapper around a synchronized item.
/// </summary>
public abstract record CanonicalItem
{
    /// <summary>
    /// Provider-assigned identity and change-tracking metadata for this item.
    /// </summary>
    public ItemProvenance Provenance { get; set; } = new();

    /// <summary>
    /// Whether this item has been deleted on the source side.
    /// </summary>
    [JsonIgnore]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Metadata bag for provider-specific or extensibility data.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string> Metadata { get; set; } = [];
}
