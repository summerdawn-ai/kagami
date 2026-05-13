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
        var rootCommand = CreateRootCommand();

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

    /// <summary>
    /// Creates the root CLI command.
    /// </summary>
    public static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Kagami — polling-first calendar and contact synchronization")
        {
            CreateJobsCommand(),
            CreateContactsCommand(),
        };

        return rootCommand;
    }

    private static Command CreateJobsCommand()
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

        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Prompt before each action is executed",
            Arity = ArgumentArity.Zero,
        };

        var jobOption = new Option<string?>("--job", "-j")
        {
            Description = "Target a specific named job key",
            Required = false,
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable more detailed logging for this run",
            Arity = ArgumentArity.Zero,
        };

        // ── jobs command group ────────────────────────────────────────────

        // jobs list
        var jobsListCommand = new Command("list", "List all configured jobs")
        {
            settingsOption,
            verboseOption,
        };
        jobsListCommand.SetAction(parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool verbose = parseResult.GetValue(verboseOption);
            using var provider = BuildServiceProvider(settingsFiles, verbose);
            var kagamiOptions = provider.GetRequiredService<KagamiOptions>();
            if (kagamiOptions.Jobs.Count == 0)
            {
                Console.WriteLine("No jobs configured.");
                return;
            }

            Console.WriteLine($"{"Key",-30} {"Enabled",-8} {"EntityType",-18} {"SourceEndpointName",-20} {"DestinationEndpointName",-20} {"Mode",-16} {"Schedule"}");
            Console.WriteLine(new string('-', 120));
            foreach (var (key, job) in kagamiOptions.Jobs)
            {
                Console.WriteLine(
                    $"{key,-30} {job.Enabled,-8} {job.EntityType,-18} {job.SourceEndpointName,-20} {job.DestinationEndpointName,-20} {job.SyncMode,-16} {job.Schedule}");
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
            confirmOption,
            jobOption,
            jobsRunOnceOption,
            jobsRunAllOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(jobsRunCommand, whatIfOption, confirmOption);
        jobsRunCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool verbose = parseResult.GetValue(verboseOption);
            var executionFlags = GetExecutionFlags(parseResult, whatIfOption, confirmOption);
            string? jobKey = parseResult.GetValue(jobOption);
            bool runOnce = parseResult.GetValue(jobsRunOnceOption);
            _ = parseResult.GetValue(jobsRunAllOption);
            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, verbose);
            var host = provider.GetRequiredService<SyncHost>();

            if (runOnce)
            {
                // One-shot mode: run targeted jobs immediately and exit.
                await host.RunOnceAsync(executionFlags, jobKey, CancellationToken.None);
            }
            else
            {
                // Default: poll continuously, optionally narrowed to a single named job.
                await host.RunContinuousAsync(executionFlags, jobKey, CancellationToken.None);
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
            verboseOption,
        };
        jobsResetCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string? jobKey = parseResult.GetValue(jobOption);
            bool resetAll = parseResult.GetValue(jobsResetAllOption);
            bool verbose = parseResult.GetValue(verboseOption);

            if (!resetAll && string.IsNullOrWhiteSpace(jobKey))
            {
                Console.Error.WriteLine("Error: Specify --job=<key> to reset a single job, or --all to reset every job.");
                return;
            }

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, verbose);
            var host = provider.GetRequiredService<SyncHost>();
            var kagamiOptions = provider.GetRequiredService<KagamiOptions>();

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
            verboseOption,
        };
        jobsUnlockCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool verbose = parseResult.GetValue(verboseOption);
            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, verbose);
            var host = provider.GetRequiredService<SyncHost>();
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

        return jobsCommand;
    }

    private static Command CreateContactsCommand()
    {
        // ── contacts command group ────────────────────────────────────────
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

        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Prompt before each action is executed",
            Arity = ArgumentArity.Zero,
        };

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

        var reverseOption = new Option<bool>("--reverse")
        {
            Description = "Sync from destination to source instead of source to destination",
            Arity = ArgumentArity.Zero,
        };

        var pruneOption = new Option<bool>("--prune")
        {
            Description = "Delete contacts on the destination that no longer exist on the source (or vice versa)",
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

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable more detailed logging for this run",
            Arity = ArgumentArity.Zero,
        };

        // contacts list
        var contactsListCommand = new Command("list", "List contacts from a configured endpoint")
        {
            settingsOption,
            fromOption,
            filterOption,
            allOption,
            verboseOption,
        };
        contactsListCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool all = parseResult.GetValue(allOption);
            bool verbose = parseResult.GetValue(verboseOption);
            var contactFilter = ContactFilter.Parse(filter);
            IReadOnlyList<CanonicalContact> contacts;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, verbose))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalContact>>();
                contacts = await handler.ListAsync(from, contactFilter, all ? null : 100, CancellationToken.None);
            }

            contacts = contacts
                .OrderBy(ContactNameHelper.GetName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (contacts.Count == 0)
            {
                Console.WriteLine("No contacts found.");
                return;
            }

            Console.WriteLine($"{"Name",-35} {"Email",-35} {"Phone"}");
            Console.WriteLine(new string('-', 95));
            foreach (var contact in contacts)
            {
                string email = contact.Emails.Count > 0 ? contact.Emails[0].Address : string.Empty;
                string phone = contact.Phones.Count > 0 ? contact.Phones[0].Number : string.Empty;
                Console.WriteLine($"{ContactNameHelper.GetNameOrId(contact),-35} {email,-35} {phone}");
            }

            Console.WriteLine();
            Console.WriteLine(all
                ? $"Total: {contacts.Count} contact(s)"
                : $"Showing {contacts.Count} contact(s) (default limit: 100; use --all to fetch everything)");
        });

        // contacts export
        var contactsExportCommand = new Command("export", "Export contacts from an endpoint to local JSON files in a directory")
        {
            settingsOption,
            fromOption,
            toEndpointOption,
            filterOption,
            pruneOption,
            verboseOption,
        };
        contactsExportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool prune = parseResult.GetValue(pruneOption);
            bool verbose = parseResult.GetValue(verboseOption);
            var contactFilter = ContactFilter.Parse(filter);
            JobExecutionResult result;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, verbose))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalContact>>();
                result = await handler.ExportAsync(from, to, prune: prune, contactFilter, CancellationToken.None);
            }

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
            reverseOption,
            pruneOption,
            onConflictOption,
            whatIfOption,
            confirmOption,
            filterOption,
            fullOption,
            forceOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(contactsSyncCommand, whatIfOption, confirmOption);
        AddMutuallyExclusiveBooleanOptionValidation(contactsSyncCommand, bidirectionalOption, reverseOption);
        contactsSyncCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            bool bidirectional = parseResult.GetValue(bidirectionalOption);
            bool reverse = parseResult.GetValue(reverseOption);
            bool prune = parseResult.GetValue(pruneOption);
            string onConflictStr = parseResult.GetValue(onConflictOption)!;
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool confirm = parseResult.GetValue(confirmOption);
            bool full = parseResult.GetValue(fullOption);
            bool force = parseResult.GetValue(forceOption);
            string? filter = parseResult.GetValue(filterOption);
            bool verbose = parseResult.GetValue(verboseOption);

            var mode = bidirectional
                ? SyncMode.Bidirectional
                : reverse
                    ? SyncMode.Reverse
                    : SyncMode.Forward;

            var contactFilter = ContactFilter.Parse(filter);

            var conflictPolicy = onConflictStr.ToLowerInvariant() switch
            {
                "source-wins" => ConflictPolicy.SourceWins,
                "dest-wins" or "destination-wins" => ConflictPolicy.DestinationWins,
                "skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.LastWriteWins,
            };

            var deletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore;

            JobExecutionResult result;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, verbose))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalContact>>();
                result = await handler.SyncAsync(from, to, mode, whatIf, confirm, contactFilter, full, force, deletePolicy, conflictPolicy, CancellationToken.None);
            }

            Console.WriteLine(result.Succeeded
                ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                : result.Error is not null
                    ? $"Sync failed: {result.Error}"
                    : $"Sync skipped: {result.SkipReason}");
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
            confirmOption,
            forceOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(contactsImportCommand, whatIfOption, confirmOption);
        contactsImportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            bool prune = parseResult.GetValue(pruneOption);
            string? filter = parseResult.GetValue(filterOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool confirm = parseResult.GetValue(confirmOption);
            bool force = parseResult.GetValue(forceOption);
            bool verbose = parseResult.GetValue(verboseOption);
            var contactFilter = ContactFilter.Parse(filter);
            JobExecutionResult result;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, verbose))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalContact>>();
                result = await handler.ImportAsync(from, to, prune, contactFilter, whatIf, confirm, force, CancellationToken.None);
            }

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

        return contactsCommand;
    }

    private static ServiceProvider BuildServiceProvider(string[] settingsFiles, bool verbose)
    {
        var configBuilder = new ConfigurationBuilder();

        // Load embedded default and custom settings.
        configBuilder.AddKagamiSettings(noDefaultSettings: false, settingsFiles, verbose);
        configBuilder.AddEnvironmentVariables();
        var config = configBuilder.Build();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            b.AddConfiguration(config.GetSection("Logging"));
        });
        services.AddKagami(config.GetSection("Kagami"));

        return services.BuildServiceProvider();
    }

    private static void AddMutuallyExclusiveBooleanOptionValidation(Command command, Option<bool> firstOption, Option<bool> secondOption) =>
        command.Validators.Add(parseResult =>
        {
            if (parseResult.GetValue(firstOption) && parseResult.GetValue(secondOption))
            {
                parseResult.AddError($"The {GetDisplayName(firstOption)} and {GetDisplayName(secondOption)} options are mutually exclusive.");
            }
        });

    private static string GetDisplayName(Option option) =>
        option.Aliases.FirstOrDefault(alias => alias.StartsWith("--", StringComparison.Ordinal)) ?? option.Name;

    private static JobExecutionFlags GetExecutionFlags(ParseResult parseResult, Option<bool> whatIfOption, Option<bool> confirmOption)
    {
        var executionFlags = JobExecutionFlags.None;

        if (parseResult.GetValue(whatIfOption))
        {
            executionFlags |= JobExecutionFlags.WhatIf;
        }

        if (parseResult.GetValue(confirmOption))
        {
            executionFlags |= JobExecutionFlags.Confirm;
        }

        return executionFlags;
    }
}
