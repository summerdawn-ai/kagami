using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provides SHA-256 content hashing for canonical items.
/// </summary>
/// <remarks>
/// Hashes are derived from the serialized canonical payload, excluding provenance fields,
/// so that two items with the same content but different provider IDs hash identically.
/// </remarks>
internal static class ContentHashHelper
{
    private const int MaximumCrossProviderEventDescriptionLength = 8_192;

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
            CanonicalEvent calendarEvent => JsonSerializer.Serialize(NormalizeEventForHash(calendarEvent), KagamiJsonContext.HashJsonOptions),
            CanonicalContact contact => JsonSerializer.Serialize(contact, KagamiJsonContext.HashJsonOptions),
            _ => throw new InvalidOperationException($"Unsupported canonical payload type '{item.GetType().FullName}'."),
        };
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Normalizes event content to the precision and capacity shared by supported providers.
    /// </summary>
    /// <remarks>
    /// Google Calendar truncates event descriptions after 8,192 characters. Retaining a longer
    /// source description in the canonical item keeps import/export lossless, while hashing the
    /// shared prefix avoids an update that Google cannot persist.
    /// </remarks>
    private static CanonicalEvent NormalizeEventForHash(CanonicalEvent item) =>
        item with
        {
            Description = item.Description is { Length: > MaximumCrossProviderEventDescriptionLength }
                ? item.Description[..MaximumCrossProviderEventDescriptionLength]
                : item.Description,
        };

    /// <summary>
    /// Ensures <see cref="ItemProvenance.ContentHash"/> is populated on <paramref name="item"/>,
    /// computing it if absent, then returns <paramref name="item"/>.
    /// </summary>
    public static TItem WithComputedHash<TItem>(TItem item) where TItem : CanonicalItem
    {
        item.Provenance.ContentHash ??= ComputeContentHash(item);
        return item;
    }

    /// <summary>
    /// Determines whether <paramref name="a"/> and <paramref name="b"/> carry identical canonical
    /// content (photo data is excluded from this comparison).
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
