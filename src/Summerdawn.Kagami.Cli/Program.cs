using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Summerdawn.Kagami.DependencyInjection;
using Summerdawn.Kagami.Engine;

namespace Summerdawn.Kagami.Cli;

/// <summary>
/// Entry point for the Kagami CLI.
/// </summary>
public static class Program
{
    /// <summary>Main entry point.</summary>
    public static int Main(string[] args)
    {
        var configOption = new Option<string?>("--config", "-c")
        {
            Description = "Path to the appsettings.json configuration file",
            Required = false,
        };

        var whatIfOption = new Option<bool>("--what-if")
        {
            Description = "Plan actions without writing any changes",
            Arity = ArgumentArity.Zero,
        };

        var jobOption = new Option<string?>("--job", "-j")
        {
            Description = "Run only the named job key",
            Required = false,
        };

        var runCommand = new Command("run", "Run continuously, polling on configured schedules")
        {
            configOption,
        };
        runCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            var host = BuildSyncHost(configPath);
            await host.RunContinuousAsync(CancellationToken.None);
        });

        var onceCommand = new Command("once", "Execute all due jobs once and exit")
        {
            configOption,
            whatIfOption,
            jobOption,
        };
        onceCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            string? jobKey = parseResult.GetValue(jobOption);
            var host = BuildSyncHost(configPath);
            await host.RunOnceAsync(whatIf, jobKey, CancellationToken.None);
        });

        var jobArgument = new Argument<string>("job-key")
        {
            Description = "The job key to reset",
        };
        var resetCommand = new Command("reset", "Reset stored sync state for a job")
        {
            jobArgument,
            configOption,
        };
        resetCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            string jobKey = parseResult.GetValue(jobArgument)!;
            var host = BuildSyncHost(configPath);
            await host.ResetJobAsync(jobKey, CancellationToken.None);
        });

        var rootCommand = new RootCommand("Kagami — polling-first calendar and contact synchronization")
        {
            runCommand,
            onceCommand,
            resetCommand,
        };

        try
        {
            return rootCommand.Parse(args).Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static SyncHost BuildSyncHost(string? configPath)
    {
        var configBuilder = new ConfigurationBuilder();
        if (configPath is not null)
        {
            configBuilder.AddJsonFile(configPath, optional: false);
        }
        else if (File.Exists("appsettings.json"))
        {
            configBuilder.AddJsonFile("appsettings.json", optional: true);
        }

        configBuilder.AddEnvironmentVariables("KAGAMI_");
        var config = configBuilder.Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddKagami(config.GetSection("Kagami"));
        services.AddFakeConnectors();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<SyncHost>();
    }
}
