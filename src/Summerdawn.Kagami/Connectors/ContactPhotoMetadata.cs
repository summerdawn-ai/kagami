using System.Security.Cryptography;

using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

internal static class ContactPhotoMetadata
{
    private const string PhotoPresenceKey = "contact.photo.presence";
    private const string PhotoBytesKey = "contact.photo.bytes";
    private const string PhotoContentTypeKey = "contact.photo.contentType";

    public static void SetPhoto(CanonicalContact contact, byte[] photoBytes, string? contentType)
    {
        contact.Metadata[PhotoPresenceKey] = "present";
        contact.Metadata[PhotoBytesKey] = Convert.ToBase64String(photoBytes);
        contact.Metadata[PhotoContentTypeKey] = NormalizeContentType(contentType, photoBytes);
    }

    public static void SetNoPhoto(CanonicalContact contact)
    {
        contact.Metadata[PhotoPresenceKey] = "absent";
        contact.Metadata.Remove(PhotoBytesKey);
        contact.Metadata.Remove(PhotoContentTypeKey);
    }

    public static bool HasKnownAbsence(CanonicalContact contact) =>
        contact.Metadata.TryGetValue(PhotoPresenceKey, out string? presence)
        && string.Equals(presence, "absent", StringComparison.Ordinal);

    /// <summary>
    /// Returns a deterministic hash of the photo on <paramref name="contact"/>.
    /// Returns <c>null</c> when photo presence has not yet been determined (loader not yet run).
    /// Returns a fixed sentinel string when the contact is known to have no photo.
    /// </summary>
    public static string? ComputePhotoHash(CanonicalContact contact)
    {
        if (!contact.Metadata.TryGetValue(PhotoPresenceKey, out string? presence))
        {
            return null;
        }

        if (string.Equals(presence, "absent", StringComparison.Ordinal))
        {
            return "absent";
        }

        if (!TryGetPhoto(contact, out byte[] photoBytes, out _))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(photoBytes));
    }

    public static bool TryGetPhoto(CanonicalContact contact, out byte[] photoBytes, out string contentType)
    {
        photoBytes = [];
        contentType = string.Empty;

        if (!contact.Metadata.TryGetValue(PhotoPresenceKey, out string? presence)
            || !string.Equals(presence, "present", StringComparison.Ordinal)
            || !contact.Metadata.TryGetValue(PhotoBytesKey, out string? encodedBytes))
        {
            return false;
        }

        try
        {
            photoBytes = Convert.FromBase64String(encodedBytes);
        }
        catch (FormatException)
        {
            return false;
        }

        contact.Metadata.TryGetValue(PhotoContentTypeKey, out string? storedContentType);
        contentType = NormalizeContentType(storedContentType, photoBytes);
        return true;
    }

    /// <summary>Returns a content-type string for a given file extension.</summary>
    public static string GetContentTypeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    /// <summary>Explicitly clears any photo metadata from the contact.</summary>
    public static void ClearPhoto(CanonicalContact contact)
    {
        contact.Metadata.Remove(PhotoBytesKey);
        contact.Metadata.Remove(PhotoContentTypeKey);
        contact.Metadata[PhotoPresenceKey] = "absent";
    }

    /// <summary>
    /// Removes all photo metadata keys so the photo is neither present nor absent —
    /// causing connectors to treat photo sync as a no-op for this contact.
    /// </summary>
    public static void DetachPhoto(CanonicalContact contact)
    {
        contact.Metadata.Remove(PhotoPresenceKey);
        contact.Metadata.Remove(PhotoBytesKey);
        contact.Metadata.Remove(PhotoContentTypeKey);
    }

    public static string GetFileExtension(string? contentType, ReadOnlySpan<byte> photoBytes)
    {
        string normalized = NormalizeContentType(contentType, photoBytes);
        return normalized switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            _ => ".bin",
        };
    }

    private static string NormalizeContentType(string? contentType, ReadOnlySpan<byte> photoBytes)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            string mediaType = contentType.Split(';', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                return mediaType;
            }
        }

        if (photoBytes.Length >= 8
            && photoBytes[0] == 0x89
            && photoBytes[1] == 0x50
            && photoBytes[2] == 0x4E
            && photoBytes[3] == 0x47
            && photoBytes[4] == 0x0D
            && photoBytes[5] == 0x0A
            && photoBytes[6] == 0x1A
            && photoBytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (photoBytes.Length >= 3
            && photoBytes[0] == 0xFF
            && photoBytes[1] == 0xD8
            && photoBytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (photoBytes.Length >= 6
            && photoBytes[0] == 0x47
            && photoBytes[1] == 0x49
            && photoBytes[2] == 0x46
            && photoBytes[3] == 0x38
            && (photoBytes[4] == 0x37 || photoBytes[4] == 0x39)
            && photoBytes[5] == 0x61)
        {
            return "image/gif";
        }

        if (photoBytes.Length >= 2
            && photoBytes[0] == 0x42
            && photoBytes[1] == 0x4D)
        {
            return "image/bmp";
        }

        if (photoBytes.Length >= 12
            && photoBytes[0] == 0x52
            && photoBytes[1] == 0x49
            && photoBytes[2] == 0x46
            && photoBytes[3] == 0x46
            && photoBytes[8] == 0x57
            && photoBytes[9] == 0x45
            && photoBytes[10] == 0x42
            && photoBytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "application/octet-stream";
    }

    /// <summary>
    /// Returns <c>true</c> if both items have a known photo hash and the hashes are equal,
    /// indicating the photo does not need to be re-uploaded during an update.
    /// </summary>
    public static bool PhotoHashesMatch(CanonicalContact? source, CanonicalContact? destination)
    {
        if (source is null || destination is null)
        {
            return false;
        }

        string? srcHash = ContactPhotoMetadata.ComputePhotoHash(source);
        string? dstHash = ContactPhotoMetadata.ComputePhotoHash(destination);
        return srcHash is not null && dstHash is not null && srcHash == dstHash;
    }
}
