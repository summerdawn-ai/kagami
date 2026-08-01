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
    /// <remarks>
    /// This property is included in JSON exports and is used to identify the provider item and
    /// track provider changes, but it is operational metadata rather than synchronized content.
    /// It is excluded from the content hash used for content comparison.
    /// </remarks>
    public ItemProvenance Provenance { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this item has been deleted on the source side.
    /// </summary>
    /// <remarks>
    /// This is an in-memory synchronization tombstone. It is not exported as JSON and is excluded
    /// from the content hash used for content comparison.
    /// </remarks>
    [JsonIgnore]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets the metadata bag for provider-specific or extensibility data.
    /// </summary>
    /// <remarks>
    /// This is in-memory connector metadata used for provider filtering and auxiliary operations
    /// such as contact photos. It is not exported as JSON and is excluded from the content hash
    /// used for content comparison.
    /// </remarks>
    [JsonIgnore]
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Returns a human-readable display string for this item.
    /// </summary>
    public virtual string ToDisplayString() =>
        string.IsNullOrWhiteSpace(Provenance.ProviderId)
            ? "unknown"
            : Provenance.ProviderId;
}
