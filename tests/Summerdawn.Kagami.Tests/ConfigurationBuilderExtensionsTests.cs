using Microsoft.Extensions.Configuration;

using Summerdawn.Kagami.DependencyInjection;

namespace Summerdawn.Kagami.Tests;

public sealed class ConfigurationBuilderExtensionsTests
{
    [Fact]
    public void AddKagamiSettings_UsesDefaultLoggingWhenVerboseIsDisabled()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: false, [], verbose: false);
        var configuration = configurationBuilder.Build();

        Assert.Equal("Information", configuration["Logging:LogLevel:Default"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
    }

    [Fact]
    public void AddKagamiSettings_OverridesLoggingWhenVerboseIsEnabled()
    {
        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddKagamiSettings(noDefaultSettings: false, [], verbose: true);
        var configuration = configurationBuilder.Build();

        Assert.Equal("Debug", configuration["Logging:LogLevel:Default"]);
        Assert.Equal("Information", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:Polly"]);
    }

    [Fact]
    public void AddKagamiSettings_PreservesCustomFileOverridesWhenVerboseIsEnabled()
    {
        string settingsFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(settingsFile, """
                {
                  "Logging": {
                    "LogLevel": {
                      "Default": "Error"
                    }
                  }
                }
                """);

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddKagamiSettings(noDefaultSettings: false, [settingsFile], verbose: true);
            var configuration = configurationBuilder.Build();

            Assert.Equal("Error", configuration["Logging:LogLevel:Default"]);
            Assert.Equal("Information", configuration["Logging:LogLevel:System.Net.Http.HttpClient"]);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }
}
