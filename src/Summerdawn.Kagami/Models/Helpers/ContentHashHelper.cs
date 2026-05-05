using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Computes and compares SHA-256 content hashes for canonical items.
/// Hashes are derived from the serialized canonical payload, excluding provenance fields,
/// so that two items with the same content but different provider IDs hash identically.
/// </summary>
internal static class ContentHashHelper
{
    /// <summary>
    /// Computes a hex-encoded SHA-256 hash of <paramref name="item"/>'s canonical payload.
    /// </summary>
    /// <remarks>
    /// The hash is computed from a JSON serialization that uses the hash-specific serializer
    /// context, which excludes provenance data and other non-canonical fields so the hash is
    /// stable across providers.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports all possible types of item")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports all possible types of item")]
    public static string ComputeContentHash(CanonicalItem item)
    {
        // Serialize with custom 'Hash' context which excludes provenance and other non-canonical data
        string json = item switch
        {
            CanonicalCalendarEvent calendarEvent => JsonSerializer.Serialize(calendarEvent, KagamiJsonContext.HashJsonOptions),
            CanonicalContact contact => JsonSerializer.Serialize(contact, KagamiJsonContext.HashJsonOptions),
            _ => throw new InvalidOperationException($"Unsupported canonical payload type '{item.GetType().FullName}'."),
        };
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Sets <see cref="ItemProvenance.ContentHash"/> on <paramref name="item"/> if it is not
    /// already populated, then returns <paramref name="item"/>.
    /// </summary>
    public static TItem WithComputedHash<TItem>(TItem item) where TItem : CanonicalItem
    {
        item.Provenance.ContentHash ??= ComputeContentHash(item);
        return item;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="a"/> and <paramref name="b"/> carry the same canonical
    /// payload (photo is excluded from this comparison).
    /// </summary>
    public static bool HaveIdenticalContent<TItem>(TItem a, TItem b) where TItem : CanonicalItem
    {
        if (a.Provenance.ContentHash is not null && b.Provenance.ContentHash is not null)
        {
            return a.Provenance.ContentHash == b.Provenance.ContentHash;
        }

        WithComputedHash(a);
        WithComputedHash(b);
        return a.Provenance.ContentHash == b.Provenance.ContentHash;
    }
}
