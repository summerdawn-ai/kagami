
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

internal static class ContactPhotoMetadata
{
    private const string PhotoPresenceKey = "contact.photo.presence";
    private const string PhotoBytesKey = "contact.photo.bytes";
    private const string PhotoContentTypeKey = "contact.photo.contentType";

    public static void SetPhoto(CanonicalItem item, byte[] photoBytes, string? contentType)
    {
        item.Metadata[PhotoPresenceKey] = "present";
        item.Metadata[PhotoBytesKey] = Convert.ToBase64String(photoBytes);
        item.Metadata[PhotoContentTypeKey] = NormalizeContentType(contentType, photoBytes);
    }

    public static void SetNoPhoto(CanonicalItem item)
    {
        item.Metadata[PhotoPresenceKey] = "absent";
        item.Metadata.Remove(PhotoBytesKey);
        item.Metadata.Remove(PhotoContentTypeKey);
    }

    public static bool HasKnownAbsence(CanonicalItem item) =>
        item.Metadata.TryGetValue(PhotoPresenceKey, out string? presence)
        && string.Equals(presence, "absent", StringComparison.Ordinal);

    public static bool TryGetPhoto(CanonicalItem item, out byte[] photoBytes, out string contentType)
    {
        photoBytes = [];
        contentType = string.Empty;

        if (!item.Metadata.TryGetValue(PhotoPresenceKey, out string? presence)
            || !string.Equals(presence, "present", StringComparison.Ordinal)
            || !item.Metadata.TryGetValue(PhotoBytesKey, out string? encodedBytes))
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

        item.Metadata.TryGetValue(PhotoContentTypeKey, out string? storedContentType);
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

    /// <summary>Explicitly clears any photo metadata from the item.</summary>
    public static void ClearPhoto(CanonicalItem item)
    {
        item.Metadata.Remove(PhotoBytesKey);
        item.Metadata.Remove(PhotoContentTypeKey);
        item.Metadata[PhotoPresenceKey] = "absent";
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
}
