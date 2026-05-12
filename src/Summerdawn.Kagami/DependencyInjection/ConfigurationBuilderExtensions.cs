using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Summerdawn.Kagami.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="IConfigurationBuilder"/>.
/// </summary>
internal static class ConfigurationBuilderExtensions
{
    private const string ResourceNamespace = "Summerdawn.Kagami";

    /// <summary>
    /// Adds Kagami settings from embedded defaults and specified settings files to the configuration.
    /// </summary>
    public static void AddKagamiSettings(this IConfigurationBuilder configurationBuilder, bool noDefaultSettings, string[] settingsFileNames, bool verbose)
    {
        // Load embedded appsettings.json as first configuration source (unless disabled)
        if (!noDefaultSettings)
        {
            configurationBuilder.AddJsonResource("appsettings.json");

            if (verbose)
            {
                configurationBuilder.AddJsonResource("appsettings.Verbose.json");
            }
        }

        // Load custom appsettings.json if specified
        configurationBuilder.AddJsonFiles(settingsFileNames);
    }

    /// <summary>
    /// Adds an embedded JSON resource as a configuration source.
    /// </summary>
    public static void AddJsonResource(this IConfigurationBuilder configurationBuilder, string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var resourceStream = assembly.GetManifestResourceStream($"{ResourceNamespace}.{resourceName}") ??
                                   throw new ArgumentException($"Resource {resourceName} not found in assembly.");

        // Copy to MemoryStream for configuration system use.
        // NOTE: The MemoryStream is intentionally NOT disposed here. The JsonStreamConfigurationProvider
        // takes ownership of the stream and will dispose it when the provider itself is disposed as part
        // of the configuration system's lifecycle.
        // This is the standard pattern for stream-based configuration sources.
        var memoryStream = new MemoryStream();
        resourceStream.CopyTo(memoryStream);
        memoryStream.Position = 0;

        configurationBuilder.Add(new JsonStreamConfigurationSource
        {
            Stream = memoryStream
        });
    }

    /// <summary>
    /// Adds the specified JSON settings files to the configuration.
    /// </summary>
    public static void AddJsonFiles(this IConfigurationBuilder configurationBuilder, string[] paths)
    {
        foreach (string settingsFile in paths)
        {
            configurationBuilder.AddJsonFile(settingsFile, optional: false);
        }
    }
}
