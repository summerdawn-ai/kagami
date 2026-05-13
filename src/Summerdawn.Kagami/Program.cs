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
            CreateContactsCommand(),
        };

        return rootCommand;
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
        var intervalOption = new Option<string?>("--interval")
        {
            Description = "Repeat the sync indefinitely with the given delay between runs (ISO 8601 duration, e.g. PT15M). Without this option the command runs once and exits.",
            Required = false,
        };

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
            intervalOption,
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
            string? intervalStr = parseResult.GetValue(intervalOption);
            bool verbose = parseResult.GetValue(verboseOption);

            TimeSpan? interval = intervalStr is not null ? ParseIntervalArgument(intervalStr) : null;

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

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, verbose);
            var handler = provider.GetRequiredService<CommandHandler<CanonicalContact>>();

            if (interval.HasValue)
            {
                // Interval mode: repeat indefinitely; exit nonzero on failure.
                // Honor process-exit signals (SIGTERM from Docker, Ctrl+C) so the delay can be interrupted.
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

                while (!cts.Token.IsCancellationRequested)
                {
                    var result = await handler.SyncAsync(from, to, mode, whatIf, confirm, contactFilter, full, force, deletePolicy, conflictPolicy, cts.Token);

                    if (!result.Succeeded && result.Error is not null)
                    {
                        throw new InvalidOperationException($"Sync failed: {result.Error}");
                    }

                    Console.WriteLine(result.Succeeded
                        ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                        : $"Sync skipped: {result.SkipReason}");

                    await Task.Delay(interval.Value, cts.Token);
                }
            }
            else
            {
                // One-shot mode: run once and exit.
                var result = await handler.SyncAsync(from, to, mode, whatIf, confirm, contactFilter, full, force, deletePolicy, conflictPolicy, CancellationToken.None);

                Console.WriteLine(result.Succeeded
                    ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                    : result.Error is not null
                        ? $"Sync failed: {result.Error}"
                        : $"Sync skipped: {result.SkipReason}");
            }
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

        // contacts reset
        var resetFromOption = new Option<string?>("--from")
        {
            Description = "Source endpoint name of the sync to reset",
            Required = false,
        };

        var resetToOption = new Option<string?>("--to")
        {
            Description = "Destination endpoint name of the sync to reset",
            Required = false,
        };

        var resetAllOption = new Option<bool>("--all")
        {
            Description = "Reset stored sync state for all contacts syncs",
            Arity = ArgumentArity.Zero,
        };

        var contactsResetCommand = new Command("reset", "Reset stored sync state (cursors and link state) for a contacts sync")
        {
            settingsOption,
            resetFromOption,
            resetToOption,
            resetAllOption,
            verboseOption,
        };
        contactsResetCommand.Validators.Add(parseResult =>
        {
            bool hasFrom = parseResult.GetValue(resetFromOption) is not null;
            bool hasTo = parseResult.GetValue(resetToOption) is not null;
            bool hasAll = parseResult.GetValue(resetAllOption);

            if (!hasAll && (!hasFrom || !hasTo))
            {
                parseResult.AddError("Specify --from <endpoint> --to <endpoint> to reset a specific sync, or --all to reset everything.");
            }

            if (hasAll && (hasFrom || hasTo))
            {
                parseResult.AddError("The --all option is mutually exclusive with --from and --to.");
            }
        });
        contactsResetCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string? from = parseResult.GetValue(resetFromOption);
            string? to = parseResult.GetValue(resetToOption);
            bool resetAll = parseResult.GetValue(resetAllOption);
            bool verbose = parseResult.GetValue(verboseOption);

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, verbose);
            var host = provider.GetRequiredService<SyncHost>();

            if (resetAll)
            {
                await host.ResetAllAsync(CancellationToken.None);
                Console.WriteLine("Reset all: sync state cleared.");
            }
            else
            {
                await host.ResetContactsSyncAsync(from!, to!, CancellationToken.None);
                Console.WriteLine($"Reset contacts sync from {from} to {to}.");
            }
        });

        // contacts unlock
        var unlockFromOption = new Option<string?>("--from")
        {
            Description = "Source endpoint name of the sync to unlock",
            Required = false,
        };

        var unlockToOption = new Option<string?>("--to")
        {
            Description = "Destination endpoint name of the sync to unlock",
            Required = false,
        };

        var unlockAllOption = new Option<bool>("--all")
        {
            Description = "Force-release all stored job leases",
            Arity = ArgumentArity.Zero,
        };

        var contactsUnlockCommand = new Command("unlock", "Force-release job leases (use after a crash to clear stuck leases)")
        {
            settingsOption,
            unlockFromOption,
            unlockToOption,
            unlockAllOption,
            verboseOption,
        };
        contactsUnlockCommand.Validators.Add(parseResult =>
        {
            bool hasFrom = parseResult.GetValue(unlockFromOption) is not null;
            bool hasTo = parseResult.GetValue(unlockToOption) is not null;
            bool hasAll = parseResult.GetValue(unlockAllOption);

            if (!hasAll && (!hasFrom || !hasTo))
            {
                parseResult.AddError("Specify --from <endpoint> --to <endpoint> to unlock a specific sync, or --all to unlock all leases.");
            }

            if (hasAll && (hasFrom || hasTo))
            {
                parseResult.AddError("The --all option is mutually exclusive with --from and --to.");
            }
        });
        contactsUnlockCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            string? from = parseResult.GetValue(unlockFromOption);
            string? to = parseResult.GetValue(unlockToOption);
            bool unlockAll = parseResult.GetValue(unlockAllOption);
            bool verbose = parseResult.GetValue(verboseOption);

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, verbose);
            var host = provider.GetRequiredService<SyncHost>();

            if (unlockAll)
            {
                int cleared = await host.UnlockAllJobsAsync(CancellationToken.None);
                Console.WriteLine(cleared > 0
                    ? $"Cleared {cleared} job lock(s)."
                    : "No locks found.");
            }
            else
            {
                int cleared = await host.UnlockContactsSyncAsync(from!, to!, CancellationToken.None);
                Console.WriteLine(cleared > 0
                    ? $"Cleared lock for contacts sync from {from} to {to}."
                    : $"No lock found for contacts sync from {from} to {to}.");
            }
        });

        var contactsCommand = new Command("contacts", "Interactive contact operations")
        {
            contactsListCommand,
            contactsExportCommand,
            contactsImportCommand,
            contactsSyncCommand,
            contactsResetCommand,
            contactsUnlockCommand,
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

    /// <summary>
    /// Parses a simplified ISO 8601 duration string (e.g. <c>PT15M</c>, <c>PT2H</c>, <c>PT30S</c>)
    /// into a <see cref="TimeSpan"/>. Throws a <see cref="FormatException"/> with a clear message
    /// if the value cannot be parsed.
    /// </summary>
    internal static TimeSpan ParseIntervalArgument(string value)
    {
        if (value.StartsWith("PT", StringComparison.OrdinalIgnoreCase) && value.Length >= 4)
        {
            string rest = value[2..];
            char unit = char.ToUpperInvariant(rest[^1]);
            if (int.TryParse(rest[..^1], out int n) && n > 0)
            {
                return unit switch
                {
                    'S' => TimeSpan.FromSeconds(n),
                    'M' => TimeSpan.FromMinutes(n),
                    'H' => TimeSpan.FromHours(n),
                    _ => throw new FormatException($"Unsupported ISO 8601 duration unit '{rest[^1]}' in '{value}'. Supported: PT<n>S, PT<n>M, PT<n>H."),
                };
            }
        }

        throw new FormatException(
            $"Cannot parse '{value}' as an ISO 8601 duration. Supported formats: PT<n>S (seconds), PT<n>M (minutes), PT<n>H (hours). Example: PT15M");
    }
}
