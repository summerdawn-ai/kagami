
using System.Text;

using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;
public static class ContactName
{
    public static string GetName(CanonicalContact contact) =>
        !string.IsNullOrWhiteSpace(contact.DisplayName)
            ? contact.DisplayName
            : contact.Organization ?? string.Empty;

    public static string GetName(CanonicalItem item) =>
        item.Payload is CanonicalContact contact
            ? GetName(contact)
            : string.Empty;

    public static string GetNameOrId(CanonicalItem item)
    {
        string name = GetName(item);
        return string.IsNullOrWhiteSpace(name) ? item.SourceId : name;
    }

    public static string BuildExportBaseName(CanonicalItem item)
    {
        string name = GetName(item);
        if (!string.IsNullOrWhiteSpace(name))
        {
            string sanitizedName = SanitizeName(name);
            if (!string.IsNullOrWhiteSpace(sanitizedName))
            {
                return sanitizedName;
            }
        }

        return SanitizeId(item.SourceId);
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
