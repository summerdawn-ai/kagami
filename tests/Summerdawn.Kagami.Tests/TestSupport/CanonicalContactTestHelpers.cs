using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Tests;

/// <summary>
/// Provides shared serialization helpers for normalization tests.
/// </summary>
internal static class CanonicalContactTestHelpers
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new KagamiJsonContext(),
    };

    private static readonly JsonSerializerOptions CompactToStringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes <paramref name="contact"/> to a compact camelCase JSON string, excluding provenance,
    /// for stable test assertions.
    /// </summary>
    internal static string SerializeCore(CanonicalContact contact)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(contact, CompactOptions))!.AsObject();
        node.Remove("provenance");
        return node.ToJsonString(CompactToStringOptions);
    }
}
