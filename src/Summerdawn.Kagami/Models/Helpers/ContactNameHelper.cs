
using System.Text;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Utility methods for deriving display-friendly names and safe filenames from a
/// <see cref="CanonicalContact"/>.
/// </summary>
public static class ContactNameHelper
{
    /// <summary>
    /// Returns the effective display name for <paramref name="contact"/>: the
    /// <see cref="CanonicalContact.DisplayName"/> when present, otherwise
    /// <see cref="CanonicalContact.Organization"/>, or an empty string when neither is set.
    /// </summary>
    public static string GetName(CanonicalContact contact) =>
        !string.IsNullOrWhiteSpace(contact.DisplayName)
            ? contact.DisplayName
            : contact.Organization ?? string.Empty;

    /// <summary>
    /// Returns the effective display name for <paramref name="item"/>, falling back to the
    /// provider ID when no name is available.
    /// </summary>
    public static string GetNameOrId(CanonicalContact item)
    {
        string name = GetName(item);
        return string.IsNullOrWhiteSpace(name) ? item.Provenance.ProviderId : name;
    }

    /// <summary>
    /// Builds a safe base filename for an exported contact JSON file.
    /// The name is derived from the contact's effective name, sanitized to ASCII lowercase with
    /// underscores replacing non-letter characters. Falls back to a sanitized provider ID when
    /// the name produces an empty result.
    /// </summary>
    public static string BuildExportBaseName(CanonicalContact contact)
    {
        string name = GetName(contact);
        if (!string.IsNullOrWhiteSpace(name))
        {
            string sanitizedName = SanitizeName(name);
            if (!string.IsNullOrWhiteSpace(sanitizedName))
            {
                return sanitizedName;
            }
        }

        return SanitizeId(contact.Provenance.ProviderId);
    }

    /// <summary>
    /// Converts a display name to a lowercase ASCII identifier by replacing non-letter characters
    /// with underscores. Leading and trailing underscores are trimmed.
    /// </summary>
    private static string SanitizeName(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z')
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

    /// <summary>
    /// Converts a provider ID string to a safe lowercase filename by keeping alphanumeric
    /// characters and converting spaces, hyphens, dots and apostrophes to underscores.
    /// Consecutive underscores are collapsed; returns <c>"unknown"</c> for an empty result.
    /// </summary>
    private static string SanitizeId(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is ' ' or '-' or '.' or '\'' && sb.Length > 0)
            {
                sb.Append('_');
            }
        }

        string result = sb.ToString().Trim('_');
        while (result.Contains("__", StringComparison.Ordinal))
        {
            result = result.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }
}
