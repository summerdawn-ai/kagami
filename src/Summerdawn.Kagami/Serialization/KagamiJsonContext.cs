using System.Text.Json;
using System.Text.Json.Serialization;

using Summerdawn.Kagami.Authentication;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Serialization;

[JsonSerializable(typeof(GoogleTokenCache))]
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
    public static readonly KagamiJsonContext Indented = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
}
