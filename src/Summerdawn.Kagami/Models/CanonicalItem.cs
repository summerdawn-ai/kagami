using System.Text.Json.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Abstract base for all canonical items. Concrete subtypes carry the domain payload;
/// provider-specific metadata lives in <see cref="Provenance"/>.
/// </summary>
public abstract class CanonicalItem
{
    /// <summary>Entity type identifier (e.g., "calendar-event", "contact").</summary>
    [JsonIgnore]
    public abstract string EntityType { get; }

    /// <summary>Provider-specific and sync-related metadata.</summary>
    [JsonIgnore]
    public ItemProvenance Provenance { get; set; } = new();

    /// <summary>
    /// Returns a shallow clone of this item with the given provenance replacing the current one.
    /// </summary>
    public abstract CanonicalItem CloneWithProvenance(ItemProvenance provenance);
}
