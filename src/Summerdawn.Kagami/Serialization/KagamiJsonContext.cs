using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Serialization;

[JsonSerializable(typeof(GoogleTokenCacheEntry))]
[JsonSerializable(typeof(CanonicalItem))]
[JsonSerializable(typeof(CanonicalEvent))]
[JsonSerializable(typeof(CalendarEventParticipant))]
[JsonSerializable(typeof(CanonicalContact))]
[JsonSerializable(typeof(ContactAddress))]
[JsonSerializable(typeof(ContactEmail))]
[JsonSerializable(typeof(ContactPhone))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal partial class KagamiJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Defines JSON serialization options for import/export.
    /// </summary>
    /// <remarks>
    /// Use this when the concrete type to be (de-)serialized is not known at compile time (i.e. is generic).
    /// </remarks>
    public static readonly JsonSerializerOptions ImportExportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Needed to enable AOT-compatible JSON serialization
        TypeInfoResolver = new KagamiJsonContext()
    };

    /// <summary>
    /// Defines JSON serialization options to compute hashes on synchronizable canonical content.
    /// </summary>
    ///
    /// <remarks>
    /// Event organizer and attendee data is retained by import/export and provider reads, but is
    /// excluded from synchronization hashes. Event writes intentionally omit those fields so a
    /// provider-owned copy cannot be mistaken for a participant-faithful meeting. Excluding them
    /// here prevents the resulting provider-specific participant state from causing repeated
    /// cross-provider updates.
    /// </remarks>
    public static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        TypeInfoResolver = new KagamiJsonContext().WithAddedModifier(typeInfo =>
        {
            if (typeof(CanonicalItem).IsAssignableFrom(typeInfo.Type))
            {
                RemoveProperty(typeInfo, "Provenance");
                RemoveProperty(typeInfo, "Metadata");
                RemoveProperty(typeInfo, "ContentHash");
            }

            if (typeInfo.Type == typeof(ItemProvenance))
            {
                typeInfo.Properties.Clear();
            }

            if (typeInfo.Type == typeof(CanonicalEvent))
            {
                RemoveProperty(typeInfo, "Organizer");
                RemoveProperty(typeInfo, "Attendees");
                RemoveProperty(typeInfo, "HasAttendees");
            }
        })
    };

    private static void RemoveProperty(JsonTypeInfo typeInfo, string propertyName)
    {
        for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (string.Equals(typeInfo.Properties[i].Name, propertyName, StringComparison.Ordinal))
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}
