using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

using Summerdawn.Kagami.Configuration;

namespace Summerdawn.Kagami.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="IConfigurationBuilder"/>.
/// </summary>
internal static class ConfigurationBuilderExtensions
{
    private const string ResourceNamespace = "Summerdawn.Kagami";

    /// <summary>
    /// Adds Kagami settings from embedded defaults, local files, environment variables, and specified settings files to the configuration.
    /// </summary>
    public static void AddKagamiSettings(this IConfigurationBuilder configurationBuilder, bool noDefaultSettings, bool noApplicationDataSettings, string[] settingsFileNames, bool verboseSettings)
    {
        // Load embedded appsettings.json as the first configuration source unless disabled.
        if (!noDefaultSettings)
        {
            configurationBuilder.AddJsonResource("appsettings.json");
        }

        configurationBuilder.AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true);
        if (!noApplicationDataSettings)
        {
            configurationBuilder.AddJsonFile(Path.Combine(KagamiOptions.DefaultDataDirectory, "appsettings.json"), optional: true);
        }
        configurationBuilder.AddEnvironmentVariables();
        configurationBuilder.AddJsonFiles(settingsFileNames);

        if (verboseSettings)
        {
            configurationBuilder.AddJsonResource("appsettings.Verbose.json");
        }
    }

    /// <summary>
    /// Adds an embedded JSON resource as a configuration source.
    /// </summary>
    public static IConfigurationBuilder AddJsonResource(this IConfigurationBuilder configurationBuilder, string resourceName, int? position = null)
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

        var source = new JsonStreamConfigurationSource
        {
            Stream = memoryStream
        };

        if (position.HasValue)
        {
            configurationBuilder.Sources.Insert(position.Value, source);
        }
        else
        {
            configurationBuilder.Sources.Add(source);
        }

        return configurationBuilder;
    }

    /// <summary>
    /// Adds the specified JSON settings files to the configuration.
    /// </summary>
    public static IConfigurationBuilder AddJsonFile(this IConfigurationBuilder configurationBuilder, string path, bool optional = false, int? position = null)
    {
        var source = new JsonConfigurationSource
        {
            Path = path,
            Optional = optional,
            ReloadOnChange = false,
        };

        source.ResolveFileProvider();

        if (position.HasValue)
        {
            configurationBuilder.Sources.Insert(position.Value, source);
        }
        else
        {
            configurationBuilder.Sources.Add(source);
        }

        return configurationBuilder;
    }

    /// <summary>
    /// Adds the specified JSON settings files to the configuration.
    /// </summary>
    public static IConfigurationBuilder AddJsonFiles(this IConfigurationBuilder configurationBuilder, IEnumerable<string> paths)
    {
        foreach (string settingsFile in paths)
        {
            configurationBuilder.AddJsonFile(settingsFile, optional: false);
        }

        return configurationBuilder;
    }
}
