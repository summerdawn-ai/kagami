namespace Summerdawn.Kagami.Connectors;

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Summerdawn.Kagami.Models;

internal static class CanonicalItemSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    [RequiresDynamicCode("Calls System.Text.Json.JsonSerializer.Serialize(Object, Type, JsonSerializerOptions)")]
    [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.Serialize(Object, Type, JsonSerializerOptions)")]
    public static string ComputeContentHash(object? payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        string json = JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    public static CanonicalItem WithComputedHash(CanonicalItem item)
    {
        item.ContentHash ??= ComputeContentHash(item.Payload);
        return item;
    }
}
