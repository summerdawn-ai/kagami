namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Extension methods for reading typed values from an endpoint's property dictionary.
/// Key lookups are case-insensitive.
/// </summary>
internal static class EndpointOptionsExtensions
{
    /// <summary>
    /// Returns the value for <paramref name="name"/> from <paramref name="properties"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the key is missing or its value is null or whitespace.
    /// </exception>
    public static string GetRequiredValue(this IReadOnlyDictionary<string, string> properties, string name, string context)
    {
        if (TryGetValue(properties, name, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Missing required property '{name}' for {context}.");
    }

    /// <summary>
    /// Returns the value for <paramref name="name"/> from <paramref name="properties"/>, or
    /// <c>null</c> when the key is missing or the value is null or whitespace.
    /// </summary>
    public static string? GetOptionalValue(this IReadOnlyDictionary<string, string> properties, string name)
    {
        return TryGetValue(properties, name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    /// <summary>
    /// Performs a case-insensitive key lookup in <paramref name="properties"/>.
    /// Returns <c>false</c> when no matching key is found.
    /// </summary>
    private static bool TryGetValue(IReadOnlyDictionary<string, string> properties, string name, out string? value)
    {
        foreach ((string key, string currentValue) in properties)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = currentValue;
                return true;
            }
        }

        value = null;
        return false;
    }
}
