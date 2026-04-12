using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.DependencyInjection;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Cli;

/// <summary>
/// Entry point for the Kagami CLI.
/// </summary>
public static class Program
{
    /// <summary>Main entry point.</summary>
    public static int Main(string[] args)
    {
        // ── Shared options ────────────────────────────────────────────────
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
            Description = "Target a specific named job key",
            Required = false,
        };

        // ── jobs command group ────────────────────────────────────────────

        // jobs list
        var jobsListCommand = new Command("list", "List all configured jobs")
        {
            configOption,
        };
        jobsListCommand.SetAction(parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            var kagamiOptions = BuildKagamiOptions(configPath);
            if (kagamiOptions.Jobs.Count == 0)
            {
                Console.WriteLine("No jobs configured.");
                return;
            }

            Console.WriteLine($"{"Key",-30} {"Enabled",-8} {"EntityType",-18} {"EndpointA",-20} {"EndpointB",-20} {"Mode",-16} {"Schedule"}");
            Console.WriteLine(new string('-', 120));
            foreach (var (key, job) in kagamiOptions.Jobs)
            {
                Console.WriteLine(
                    $"{key,-30} {job.Enabled,-8} {job.EntityType,-18} {job.EndpointA,-20} {job.EndpointB,-20} {job.SyncMode,-16} {job.Schedule}");
            }
        });

        // jobs run
        var jobsRunOnceOption = new Option<bool>("--once")
        {
            Description = "Execute all due jobs once and exit (default behavior; provide --all for continuous mode)",
            Arity = ArgumentArity.Zero,
        };

        var jobsRunAllOption = new Option<bool>("--all")
        {
            Description = "Run continuously, polling all jobs on their configured schedules",
            Arity = ArgumentArity.Zero,
        };

        var jobsRunCommand = new Command("run", "Run configured sync jobs")
        {
            configOption,
            whatIfOption,
            jobOption,
            jobsRunOnceOption,
            jobsRunAllOption,
        };
        jobsRunCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            string? jobKey = parseResult.GetValue(jobOption);
            bool runAll = parseResult.GetValue(jobsRunAllOption);
            var host = BuildSyncHost(configPath);

            if (runAll)
            {
                // Continuous mode: run until cancelled
                await host.RunContinuousAsync(CancellationToken.None);
            }
            else
            {
                // Default: run all due jobs once (or a single named job)
                await host.RunOnceAsync(whatIf, jobKey, CancellationToken.None);
            }
        });

        // jobs reset
        var jobsResetAllOption = new Option<bool>("--all")
        {
            Description = "Reset stored sync state for all configured jobs",
            Arity = ArgumentArity.Zero,
        };

        var jobsResetCommand = new Command("reset", "Reset stored sync state for one or all jobs")
        {
            configOption,
            jobOption,
            jobsResetAllOption,
        };
        jobsResetCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            string? jobKey = parseResult.GetValue(jobOption);
            bool resetAll = parseResult.GetValue(jobsResetAllOption);

            if (!resetAll && string.IsNullOrWhiteSpace(jobKey))
            {
                Console.Error.WriteLine("Error: Specify --job=<key> to reset a single job, or --all to reset every job.");
                return;
            }

            var host = BuildSyncHost(configPath);
            var kagamiOptions = BuildKagamiOptions(configPath);

            if (resetAll)
            {
                foreach (string key in kagamiOptions.Jobs.Keys)
                {
                    await host.ResetJobAsync(key, CancellationToken.None);
                    Console.WriteLine($"Reset job: {key}");
                }
            }
            else
            {
                await host.ResetJobAsync(jobKey!, CancellationToken.None);
                Console.WriteLine($"Reset job: {jobKey}");
            }
        });

        var jobsCommand = new Command("jobs", "Manage and run configured sync jobs")
        {
            jobsListCommand,
            jobsRunCommand,
            jobsResetCommand,
        };

        // ── contacts command group ────────────────────────────────────────

        var fromOption = new Option<string>("--from")
        {
            Description = "Source endpoint name (as configured in appsettings.json)",
            Required = true,
        };

        var toEndpointOption = new Option<string>("--to")
        {
            Description = "Destination endpoint name or local directory path",
            Required = true,
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "OData-style filter expression, e.g. startswith(name,'A')",
            Required = false,
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Re-sync all in-scope contacts even if not changed (ignores HasChanged short-circuit)",
            Arity = ArgumentArity.Zero,
        };

        var modeOption = new Option<string>("--mode")
        {
            Description = "Sync direction: bidi (default), a-to-b, b-to-a",
            Required = false,
            DefaultValueFactory = _ => "bidi",
        };

        // contacts list
        var contactsListCommand = new Command("list", "List contacts from a configured endpoint")
        {
            configOption,
            fromOption,
            filterOption,
        };
        contactsListCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            string from = parseResult.GetValue(fromOption)!;
            string? filter = parseResult.GetValue(filterOption);
            var svc = BuildContactsService(configPath);
            var contactFilter = ContactFilter.Parse(filter);
            var items = await svc.ListAsync(from, contactFilter, CancellationToken.None);
            if (items.Count == 0)
            {
                Console.WriteLine("No contacts found.");
                return;
            }

            Console.WriteLine($"{"Display Name",-35} {"Email",-35} {"Phone"}");
            Console.WriteLine(new string('-', 95));
            foreach (var item in items)
            {
                if (item.Payload is not CanonicalContact contact)
                {
                    Console.WriteLine($"  (non-contact item: {item.SourceId})");
                    continue;
                }

                string email = contact.Emails.Count > 0 ? contact.Emails[0].Address : string.Empty;
                string phone = contact.Phones.Count > 0 ? contact.Phones[0].Number : string.Empty;
                Console.WriteLine($"{contact.DisplayName,-35} {email,-35} {phone}");
            }

            Console.WriteLine();
            Console.WriteLine($"Total: {items.Count} contact(s)");
        });

        // contacts export
        var contactsExportCommand = new Command("export", "Export contacts from an endpoint to local JSON files")
        {
            configOption,
            fromOption,
            toEndpointOption,
            filterOption,
        };
        contactsExportCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            string? filter = parseResult.GetValue(filterOption);
            var svc = BuildContactsService(configPath);
            var contactFilter = ContactFilter.Parse(filter);
            await svc.ExportAsync(from, to, contactFilter, CancellationToken.None);
        });

        // contacts sync
        var contactsSyncCommand = new Command("sync", "Synchronize contacts between two configured endpoints")
        {
            configOption,
            fromOption,
            toEndpointOption,
            modeOption,
            whatIfOption,
            filterOption,
            forceOption,
        };
        contactsSyncCommand.SetAction(async parseResult =>
        {
            string? configPath = parseResult.GetValue(configOption);
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            string modeStr = parseResult.GetValue(modeOption)!;
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool force = parseResult.GetValue(forceOption);
            string? filter = parseResult.GetValue(filterOption);

            SyncMode mode = modeStr.ToLowerInvariant() switch
            {
                "bidi" or "bidirectional" => SyncMode.Bidirectional,
                "a-to-b" or "atob" => SyncMode.AToB,
                "b-to-a" or "btoa" => SyncMode.BToA,
                _ => throw new ArgumentException($"Unknown sync mode '{modeStr}'. Use: bidi, a-to-b, b-to-a"),
            };

            var svc = BuildContactsService(configPath);
            var contactFilter = ContactFilter.Parse(filter);
            var result = await svc.SyncAsync(from, to, mode, whatIf, contactFilter, force, CancellationToken.None);
            Console.WriteLine(result.Succeeded
                ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                : $"Sync failed or skipped: {result.SkipReason}");
        });

        var contactsCommand = new Command("contacts", "Interactive contact operations")
        {
            contactsListCommand,
            contactsExportCommand,
            contactsSyncCommand,
        };

        // ── Root command ──────────────────────────────────────────────────

        var rootCommand = new RootCommand("Kagami — polling-first calendar and contact synchronization")
        {
            jobsCommand,
            contactsCommand,
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

    private static ServiceProvider BuildServiceProvider(string? configPath)
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
        return services.BuildServiceProvider();
    }

    private static SyncHost BuildSyncHost(string? configPath) =>
        BuildServiceProvider(configPath).GetRequiredService<SyncHost>();

    private static ContactsService BuildContactsService(string? configPath) =>
        BuildServiceProvider(configPath).GetRequiredService<ContactsService>();

    private static KagamiOptions BuildKagamiOptions(string? configPath) =>
        BuildServiceProvider(configPath).GetRequiredService<KagamiOptions>();
}

