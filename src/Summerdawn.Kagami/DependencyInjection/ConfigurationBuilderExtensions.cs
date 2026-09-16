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
    /// Adds Kagami settings from embedded defaults, application data, and specified settings files.
    /// </summary>
    public static IConfigurationBuilder AddKagamiSettings(this IConfigurationBuilder configurationBuilder, bool noDefaultSettings, bool noApplicationDataSettings, string[] settingsFileNames, bool verboseSettings)
    {
        // Always skip application-data settings if explicit settings files are specified.
        noApplicationDataSettings = noApplicationDataSettings || settingsFileNames.Length > 0;

        if (!noDefaultSettings)
        {
            configurationBuilder.AddJsonResource("appsettings.Default.json", position: 0);
        }

        if (!noApplicationDataSettings)
        {
            // Host builders already add environment variables; keep settings files below that source.
            int? environmentVariablesPosition = configurationBuilder.GetEnvironmentVariablesPosition();
            configurationBuilder.AddJsonFile(Path.Combine(KagamiOptions.DefaultDataDirectory, "appsettings.json"), optional: true, environmentVariablesPosition);
        }

        if (settingsFileNames.Length > 0)
        {
            // Host builders already add environment variables; keep settings files below that source.
            int? environmentVariablesPosition = configurationBuilder.GetEnvironmentVariablesPosition();
            configurationBuilder.AddJsonFiles(settingsFileNames, environmentVariablesPosition);
        }

        if (verboseSettings)
        {
            configurationBuilder.AddJsonResource("appsettings.Verbose.json");
        }

        return configurationBuilder;
    }

    /// <summary>
    /// Adds an embedded JSON resource to the configuration pipeline.
    /// </summary>
    public static IConfigurationBuilder AddJsonResource(this IConfigurationBuilder configurationBuilder, string resourceName, int? position = null)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var resourceStream = assembly.GetManifestResourceStream($"{ResourceNamespace}.{resourceName}")
            ?? throw new ArgumentException($"Resource '{resourceName}' not found in assembly.");

        // Copy to MemoryStream for configuration system use.
        // NOTE: ConfigurationManager may dispose and then reload inserted stream sources.
        // Use NeverClosingMemoryStream so the embedded JSON source survives later source insertion.
        var memoryStream = new NeverClosingMemoryStream();
        resourceStream.CopyTo(memoryStream);
        memoryStream.Position = 0;

        var source = new JsonStreamConfigurationSource
        {
            Stream = memoryStream,
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
    /// Adds a JSON file to the configuration pipeline.
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
    /// Adds JSON files to the configuration pipeline.
    /// </summary>
    public static IConfigurationBuilder AddJsonFiles(this IConfigurationBuilder configurationBuilder, IEnumerable<string> paths, int? position = null)
    {
        int? currentPosition = position;

        foreach (string path in paths)
        {
            configurationBuilder.AddJsonFile(path, optional: false, currentPosition);

            if (currentPosition.HasValue)
            {
                currentPosition++;
            }
        }

        return configurationBuilder;
    }

    /// <summary>
    /// Gets the position of the environment variables configuration source.
    /// </summary>
    private static int? GetEnvironmentVariablesPosition(this IConfigurationBuilder configurationBuilder)
    {
        int position = configurationBuilder.Sources
            .ToList()
            .FindIndex(source => source.GetType().Name == "EnvironmentVariablesConfigurationSource");

        return position < 0 ? null : position;
    }

    private sealed class NeverClosingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            Seek(0, SeekOrigin.Begin);
        }

        public override ValueTask DisposeAsync()
        {
            Seek(0, SeekOrigin.Begin);
            return default;
        }
    }
}
