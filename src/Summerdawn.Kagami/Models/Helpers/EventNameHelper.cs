using System.Text;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Provides utility methods for deriving export filenames from a
/// <see cref="CanonicalEvent"/>.
/// </summary>
public static class EventNameHelper
{
    private const int MaxTitleLength = 20;

    /// <summary>
    /// Builds a safe base filename for an exported event JSON file.
    /// </summary>
    /// <remarks>
    /// The name is derived from a sanitized lowercase title truncated to 20 characters, followed
    /// by the event start date and time. When the title produces an empty result, the provider ID
    /// is used instead.
    /// </remarks>
    public static string BuildExportBaseName(CanonicalEvent item)
    {
        string title = SanitizeText(item.Title);
        string baseName = string.IsNullOrWhiteSpace(title)
            ? SanitizeText(item.Provenance.ProviderId)
            : title;

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "unknown";
        }

        if (baseName.Length > MaxTitleLength)
        {
            baseName = baseName[..MaxTitleLength].TrimEnd('_');
        }

        string startStamp = item.From == DateTimeOffset.MinValue
            ? "unknown_date_0000"
            : item.From.ToUniversalTime().ToString("yyyyMMdd_HHmm");

        return $"{baseName}_{startStamp}";
    }

    private static string SanitizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder sb = new(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim('_');
    }
}
