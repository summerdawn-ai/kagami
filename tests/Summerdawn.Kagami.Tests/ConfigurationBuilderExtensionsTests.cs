using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.DependencyInjection;

namespace Summerdawn.Kagami.Tests;

public sealed class ConfigurationBuilderExtensionsTests
{
    [Fact]
    public void AddKagamiSettings_LoadsDefaultLoggingWhenVerboseIsDisabled()
    {
        var configuration = new ConfigurationManager();
        configuration.AddKagamiSettings(noDefaultSettings: false, noApplicationDataSettings: true, [], verboseSettings: false);

        Assert.Equal("Information", configuration["Logging:LogLevel:Default"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
    }

    [Fact]
    public void AddKagamiSettings_AppliesVerboseLoggingSettingsLast()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: false, noApplicationDataSettings: true, [], verboseSettings: true);
        var configuration = configurationBuilder.Build();

        Assert.Equal("Debug", configuration["Logging:LogLevel:Summerdawn.Kagami"]);
        Assert.Equal("Information", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:Polly"]);
    }

    [Fact]
    public void AddKagamiSettings_AppliesExplicitSettingsBeforeVerboseSettings()
    {
        string settingsFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(settingsFile, """
                {
                  "Logging": {
                    "LogLevel": {
                        "Default": "Error",
                        "System.Net.Http.HttpClient": "Warning"
                    }
                  }
                }
                """);

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddKagamiSettings(noDefaultSettings: false, noApplicationDataSettings: true, [settingsFile], verboseSettings: true);
            var configuration = configurationBuilder.Build();

            Assert.Equal("Error", configuration["Logging:LogLevel:Default"]);
            Assert.Equal("Information", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
        }
        finally
        {
            if (File.Exists(settingsFile))
            {
                File.Delete(settingsFile);
            }
        }
    }

    [Fact]
    public void AddKagamiSettings_AppliesEnvironmentVariablesAfterExplicitSettings()
    {
        string settingsFile = WriteSettingsFile("Value", "Explicit");
        const string environmentVariableName = "KAGAMI_CONFIGURATION_TEST_Value";

        try
        {
            Environment.SetEnvironmentVariable(environmentVariableName, "Environment");

            var configuration = new ConfigurationManager();
            configuration.AddEnvironmentVariables("KAGAMI_CONFIGURATION_TEST_");
            configuration.AddKagamiSettings(noDefaultSettings: true, noApplicationDataSettings: true, [settingsFile], verboseSettings: false);

            Assert.Equal("Environment", configuration["Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariableName, null);
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void AddKagamiSettings_SkipsApplicationDataSettingsWhenExplicitSettingsAreSupplied()
    {
        string settingsFile = WriteSettingsFile("TestSetting", "Explicit");

        try
        {
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddKagamiSettings(noDefaultSettings: true, noApplicationDataSettings: false, [settingsFile], verboseSettings: false);

            string[] jsonPaths = GetJsonPaths(configurationBuilder);

            Assert.Contains(settingsFile, jsonPaths);
            Assert.DoesNotContain(Path.Combine(KagamiOptions.DefaultDataDirectory, "appsettings.json"), jsonPaths);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public void AddKagamiSettings_LoadsApplicationDataSettingsWhenExplicitSettingsAreNotSupplied()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: true, noApplicationDataSettings: false, [], verboseSettings: false);

        string[] jsonPaths = GetJsonPaths(configurationBuilder);

        Assert.Contains(Path.Combine(KagamiOptions.DefaultDataDirectory, "appsettings.json"), jsonPaths);
    }

    [Fact]
    public void AddKagamiSettings_DoesNotLoadEmbeddedDefaultsWhenDisabled()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: true, noApplicationDataSettings: true, [], verboseSettings: false);
        var configuration = configurationBuilder.Build();

        Assert.Null(configuration["Logging:LogLevel:Default"]);
    }

    private static string WriteSettingsFile(string settingName, string settingValue)
    {
        string settingsFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(settingsFile, $$"""
            {
              "{{settingName}}": "{{settingValue}}"
            }
            """);

        return settingsFile;
    }

    private static string[] GetJsonPaths(ConfigurationBuilder configurationBuilder)
    {
        return configurationBuilder.Sources
            .OfType<JsonConfigurationSource>()
            .Select(source => source.FileProvider?.GetFileInfo(source.Path!).PhysicalPath)
            .OfType<string>()
            .ToArray();
    }
}
