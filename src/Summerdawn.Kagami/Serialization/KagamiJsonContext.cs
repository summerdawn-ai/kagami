using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Encodings.Web;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Serialization;

[JsonSerializable(typeof(GoogleTokenCache))]
[JsonSerializable(typeof(CanonicalItem))]
[JsonSerializable(typeof(CanonicalCalendarEvent))]
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
    /// Defines JSON serialization options to compute hashes on canonical properties without provider-specific data.
    /// </summary>
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
