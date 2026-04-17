namespace Summerdawn.Kagami.Connectors;

internal static class ConnectorOptionExtensions
{
    public static string GetRequiredValue(this IReadOnlyDictionary<string, string> properties, string name, string context)
    {
        if (TryGetValue(properties, name, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"Missing required property '{name}' for {context}.");
    }

    public static string? GetOptionalValue(this IReadOnlyDictionary<string, string> properties, string name)
    {
        return TryGetValue(properties, name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

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
