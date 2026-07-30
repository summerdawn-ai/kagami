using Microsoft.Extensions.Configuration;

using Summerdawn.Kagami.DependencyInjection;

namespace Summerdawn.Kagami.Tests;

public sealed class ConfigurationBuilderExtensionsTests
{
    [Fact]
    public void AddKagamiSettings_LoadsDefaultLoggingWhenVerboseIsDisabled()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: false, noApplicationDataSettings: true, [], verboseSettings: false);
        var configuration = configurationBuilder.Build();

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
    public void AddKagamiSettings_DoesNotLoadEmbeddedDefaultsWhenDisabled()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: true, noApplicationDataSettings: true, [], verboseSettings: false);
        var configuration = configurationBuilder.Build();

        Assert.Null(configuration["Logging:LogLevel:Default"]);
    }

}
