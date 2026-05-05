
using System.Text;

namespace Summerdawn.Kagami.Models;

public static class ContactNameHelper
{
    public static string GetName(CanonicalContact contact) =>
        !string.IsNullOrWhiteSpace(contact.DisplayName)
            ? contact.DisplayName
            : contact.Organization ?? string.Empty;

    public static string GetNameOrId(CanonicalContact item)
    {
        string name = GetName(item);
        return string.IsNullOrWhiteSpace(name) ? item.Provenance.ProviderId : name;
    }

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
