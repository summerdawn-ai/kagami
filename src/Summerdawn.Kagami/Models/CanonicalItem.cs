using System.Text.Json.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provider-neutral wrapper around a synchronized item.
/// </summary>
public abstract record CanonicalItem
{
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
