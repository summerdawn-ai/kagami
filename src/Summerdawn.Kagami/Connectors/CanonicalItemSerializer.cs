
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Connectors;
internal static class CanonicalItemSerializer
{
    public static string ComputeContentHash(object? payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        string json = payload switch
        {
            CanonicalCalendarEvent calendarEvent => JsonSerializer.Serialize(calendarEvent, KagamiJsonContext.Default.CanonicalCalendarEvent),
            CanonicalContact contact => JsonSerializer.Serialize(contact, KagamiJsonContext.Default.CanonicalContact),
            _ => throw new InvalidOperationException($"Unsupported canonical payload type '{payload.GetType().FullName}'."),
        };
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    public static CanonicalItem WithComputedHash(CanonicalItem item)
    {
        item.ContentHash ??= ComputeContentHash(item.Payload);
        return item;
    }
}
