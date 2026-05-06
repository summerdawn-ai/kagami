using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.DependencyInjection;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;

using ContactFilter = Summerdawn.Kagami.Models.ContactFilter;

namespace Summerdawn.Kagami;

/// <summary>
/// Entry point for the Kagami CLI.
/// </summary>
public static class Program
{
    /// <summary>Main entry point.</summary>
    public static int Main(string[] args)
    {
        // ── Shared options ────────────────────────────────────────────────
        var settingsOption = new Option<string[]>("--settings")
        {
            Description = "Path to one or more settings JSON files to load",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
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
            settingsOption,
        };
        jobsListCommand.SetAction(parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            var kagamiOptions = BuildKagamiOptions(settingsFiles);
            if (kagamiOptions.Jobs.Count == 0)
            {
                Console.WriteLine("No jobs configured.");
                return;
            }

            Console.WriteLine($"{"Key",-30} {"Enabled",-8} {"EntityType",-18} {"Source",-20} {"Destination",-20} {"Mode",-16} {"Schedule"}");
            Console.WriteLine(new string('-', 120));
            foreach (var (key, job) in kagamiOptions.Jobs)
            {
                Console.WriteLine(
                    $"{key,-30} {job.Enabled,-8} {job.EntityType,-18} {job.Source,-20} {job.Destination,-20} {job.SyncMode,-16} {job.Schedule}");
            }
        });

        // jobs run
        var jobsRunOnceOption = new Option<bool>("--once")
        {
            Description = "Execute the targeted jobs immediately once and exit (ignores configured schedules)",
            Arity = ArgumentArity.Zero,
        };

        var jobsRunAllOption = new Option<bool>("--all")
        {
            Description = "Accepted for compatibility; continuous polling is already the default behavior",
            Arity = ArgumentArity.Zero,
        };

        var jobsRunCommand = new Command("run", "Run configured sync jobs")
        {
            settingsOption,
            whatIfOption,
            jobOption,
            jobsRunOnceOption,
            jobsRunAllOption,
        };
        jobsRunCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool whatIf = parseResult.GetValue(whatIfOption);
            string? jobKey = parseResult.GetValue(jobOption);
            bool runOnce = parseResult.GetValue(jobsRunOnceOption);
            _ = parseResult.GetValue(jobsRunAllOption);
            var host = BuildSyncHost(settingsFiles);

            if (runOnce)
            {
                // One-shot mode: run targeted jobs immediately and exit.
                await host.RunOnceAsync(whatIf, jobKey, CancellationToken.None);
            }
            else
            {
                // Default: poll continuously, optionally narrowed to a single named job.
                await host.RunContinuousAsync(whatIf, jobKey, CancellationToken.None);
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
            settingsOption,
            jobOption,
            jobsResetAllOption,
        };
        jobsResetCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string? jobKey = parseResult.GetValue(jobOption);
            bool resetAll = parseResult.GetValue(jobsResetAllOption);

            if (!resetAll && string.IsNullOrWhiteSpace(jobKey))
            {
                Console.Error.WriteLine("Error: Specify --job=<key> to reset a single job, or --all to reset every job.");
                return;
            }

            var host = BuildSyncHost(settingsFiles);
            var kagamiOptions = BuildKagamiOptions(settingsFiles);

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

        // jobs unlock
        var jobsUnlockCommand = new Command("unlock", "Force-release all job locks (use after a crash to clear stuck leases)")
        {
            settingsOption,
        };
        jobsUnlockCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            var host = BuildSyncHost(settingsFiles);
            int cleared = await host.UnlockAllJobsAsync(CancellationToken.None);
            Console.WriteLine(cleared > 0
                ? $"Cleared {cleared} job lock(s)."
                : "No locks found.");
        });

        var jobsCommand = new Command("jobs", "Manage and run configured sync jobs")
        {
            jobsListCommand,
            jobsRunCommand,
            jobsResetCommand,
            jobsUnlockCommand,
        };

        // ── contacts command group ────────────────────────────────────────

        var fromOption = new Option<string>("--from")
        {
            Description = "Source endpoint name (as configured in the settings file)",
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
            Description = "Ignore cursors, bypass change and content-sameness checks, and write all in-scope contacts unconditionally (clobbers destination drift)",
            Arity = ArgumentArity.Zero,
        };

        var fullOption = new Option<bool>("--full")
        {
            Description = "Ignore saved cursors and fetch all rows from both sides, but still skip contacts whose content is already identical",
            Arity = ArgumentArity.Zero,
        };

        var bidirectionalOption = new Option<bool>("--bidirectional")
        {
            Description = "Sync in both directions (default: source to destination only)",
            Arity = ArgumentArity.Zero,
        };

        var pruneOption = new Option<bool>("--prune")
        {
            Description = "Delete contacts on the destination that no longer exist on the source",
            Arity = ArgumentArity.Zero,
        };

        var onConflictOption = new Option<string>("--on-conflict")
        {
            Description = "Conflict resolution policy: last-write-wins (default), source-wins, dest-wins, skip",
            Required = false,
            DefaultValueFactory = _ => "last-write-wins",
        }.AcceptOnlyFromAmong("last-write-wins", "source-wins", "destination-wins", "skip");

        var allOption = new Option<bool>("--all")
        {
            Description = "Fetch and display all matching contacts",
            Arity = ArgumentArity.Zero,
        };

        // contacts list
        var contactsListCommand = new Command("list", "List contacts from a configured endpoint")
        {
            settingsOption,
            fromOption,
            filterOption,
            allOption,
        };
        contactsListCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool all = parseResult.GetValue(allOption);
            var svc = BuildContactsService(settingsFiles);
            var contactFilter = ContactFilter.Parse(filter);
            var items = await svc.ListAsync(from, contactFilter, all ? null : 100, CancellationToken.None);
            items = items
                .OrderBy(ContactNameHelper.GetName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (items.Count == 0)
            {
                Console.WriteLine("No contacts found.");
                return;
            }

            Console.WriteLine($"{"Name",-35} {"Email",-35} {"Phone"}");
            Console.WriteLine(new string('-', 95));
            foreach (var item in items)
            {
                if (item is not CanonicalContact contact)
                {
                    Console.WriteLine($"  (non-contact item: {item.Provenance.ProviderId})");
                    continue;
                }

                string email = contact.Emails.Count > 0 ? contact.Emails[0].Address : string.Empty;
                string phone = contact.Phones.Count > 0 ? contact.Phones[0].Number : string.Empty;
                Console.WriteLine($"{ContactNameHelper.GetNameOrId(item),-35} {email,-35} {phone}");
            }

            Console.WriteLine();
            Console.WriteLine(all
                ? $"Total: {items.Count} contact(s)"
                : $"Showing {items.Count} contact(s) (default limit: 100; use --all to fetch everything)");
        });

        // contacts export
        var contactsExportCommand = new Command("export", "Export contacts from an endpoint to local JSON files in a directory")
        {
            settingsOption,
            fromOption,
            toEndpointOption,
            filterOption,
            pruneOption,
        };
        contactsExportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool prune = parseResult.GetValue(pruneOption);
            var svc = BuildContactsService(settingsFiles);
            var contactFilter = ContactFilter.Parse(filter);
            var result = await svc.ExportAsync(from, to, prune: prune, contactFilter, CancellationToken.None);
            Console.WriteLine(result.Succeeded
                ? $"Export completed. Actions planned: {result.ActionsPlanned}"
                : $"Export failed: {result.Error}");
        });

        // contacts sync
        var contactsSyncCommand = new Command("sync", "Synchronize contacts between two configured endpoints")
        {
            settingsOption,
            fromOption,
            toEndpointOption,
            bidirectionalOption,
            pruneOption,
            onConflictOption,
            whatIfOption,
            filterOption,
            fullOption,
            forceOption,
        };
        contactsSyncCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            bool bidirectional = parseResult.GetValue(bidirectionalOption);
            bool prune = parseResult.GetValue(pruneOption);
            string onConflictStr = parseResult.GetValue(onConflictOption)!;
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool full = parseResult.GetValue(fullOption);
            bool force = parseResult.GetValue(forceOption);
            string? filter = parseResult.GetValue(filterOption);

            var mode = bidirectional ? SyncMode.Bidirectional : SyncMode.Forward;

            var conflictPolicy = onConflictStr.ToLowerInvariant() switch
            {
                "source-wins" => ConflictPolicy.SourceWins,
                "dest-wins" or "destination-wins" => ConflictPolicy.DestinationWins,
                "skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.LastWriteWins,
            };

            var deletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore;

            var svc = BuildContactsService(settingsFiles);
            var contactFilter = ContactFilter.Parse(filter);
            var result = await svc.SyncAsync(from, to, mode, whatIf, contactFilter, full, force, deletePolicy, conflictPolicy, CancellationToken.None);
            Console.WriteLine(result.Succeeded
                ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                : $"Sync failed or skipped: {result.SkipReason}");
        });

        // contacts import
        var contactsImportCommand = new Command("import", "Import contacts from local JSON files in a directory into a configured endpoint")
        {
            settingsOption,
            fromOption,
            toEndpointOption,
            pruneOption,
            filterOption,
            whatIfOption,
            forceOption,
        };
        contactsImportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            bool prune = parseResult.GetValue(pruneOption);
            string? filter = parseResult.GetValue(filterOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool force = parseResult.GetValue(forceOption);
            var svc = BuildContactsService(settingsFiles);
            var contactFilter = ContactFilter.Parse(filter);
            var result = await svc.ImportAsync(from, to, prune, contactFilter, whatIf, force, CancellationToken.None);
            Console.WriteLine(result.Succeeded
                ? $"Import completed. Actions planned: {result.ActionsPlanned}"
                : $"Import failed: {result.Error ?? result.SkipReason}");
        });

        var contactsCommand = new Command("contacts", "Interactive contact operations")
        {
            contactsListCommand,
            contactsExportCommand,
            contactsImportCommand,
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

    private static ServiceProvider BuildServiceProvider(string[] settingsFiles)
    {
        var configBuilder = new ConfigurationBuilder();

        // Load embedded default and custom settings.
        configBuilder.AddKagamiSettings(noDefaultSettings: false, settingsFiles);
        configBuilder.AddEnvironmentVariables();
        var config = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().AddConfiguration(config.GetSection("Logging")));
        services.AddKagami(config.GetSection("Kagami"));

        return services.BuildServiceProvider();
    }

    private static SyncHost BuildSyncHost(string[] settingsFiles) =>
        BuildServiceProvider(settingsFiles).GetRequiredService<SyncHost>();

    private static CommandHandler<CanonicalContact> BuildContactsService(string[] settingsFiles) =>
        BuildServiceProvider(settingsFiles).GetRequiredService<CommandHandler<CanonicalContact>>();

    private static KagamiOptions BuildKagamiOptions(string[] settingsFiles) =>
        BuildServiceProvider(settingsFiles).GetRequiredService<KagamiOptions>();
}
