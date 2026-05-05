using System.Text.Json.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Represents a provider-neutral synchronized item.
/// </summary>
public abstract record CanonicalItem
{
    /// <summary>
    /// Gets or sets the provider-assigned identity and change-tracking metadata for this item.
    /// </summary>
    public ItemProvenance Provenance { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this item has been deleted on the source side.
    /// </summary>
    [JsonIgnore]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the metadata bag for provider-specific or extensibility data.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string> Metadata { get; set; } = [];
}
