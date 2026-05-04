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
    /// Adds Kagami settings from various sources to the configuration.
    /// </summary>
    /// <param name="configurationBuilder">The configuration builder to add the sources to.</param>
    /// <param name="noDefaultSettings">Whether to skip loading embedded default settings.</param>
    /// <param name="settingsFileNames">Array of settings file paths to load.</param>
    public static void AddKagamiSettings(this IConfigurationBuilder configurationBuilder, bool noDefaultSettings, string[] settingsFileNames)
    {
        // Load embedded appsettings.json as first configuration source (unless disabled)
        if (!noDefaultSettings)
        {
            configurationBuilder.AddJsonResource("appsettings.json");
        }

        // Load custom appsettings.json if specified
        configurationBuilder.AddJsonFiles(settingsFileNames);
    }

    /// <summary>
    /// Adds the specified embedded resource as the first configuration source.
    /// </summary>
    /// <param name="configurationBuilder">The configuration builder to add the source to.</param>
    /// <param name="resourceName">The name of the resource in the executing assembly.</param>
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

        // Insert at position 0 to make it the first source
        configurationBuilder.Sources.Insert(0, new JsonStreamConfigurationSource
        {
            Stream = memoryStream
        });
    }

    /// <summary>
    /// Adds the specified settings files to the configuration.
    /// </summary>
    /// <param name="configurationBuilder">The configuration builder to add the sources to.</param>
    /// <param name="paths">Array of settings file paths to load.</param>
    public static void AddJsonFiles(this IConfigurationBuilder configurationBuilder, string[] paths)
    {
        foreach (string settingsFile in paths)
        {
            configurationBuilder.AddJsonFile(settingsFile, optional: false);
        }
    }
}
