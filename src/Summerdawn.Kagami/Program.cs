using System.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.DependencyInjection;
using Summerdawn.Kagami.Engine;
using Summerdawn.Kagami.Models;

using CalendarEventFilter = Summerdawn.Kagami.Models.CalendarEventFilter;
using ContactFilter = Summerdawn.Kagami.Models.ContactFilter;

namespace Summerdawn.Kagami;

/// <summary>
/// Entry point for the Kagami CLI.
/// </summary>
public static class Program
{
    private const int EventTitleColumnWidth = 40;
    private const int EventDateColumnWidth = 22;
    private const int EventLocationColumnWidth = 30;

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
            CreateEventsCommand(),
            CreateJobsCommand(),
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

        var noDefaultSettingsOption = new Option<bool>("--no-default-settings")
        {
            Description = "Skip loading embedded default settings",
            Arity = ArgumentArity.Zero,
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
            Description = "Conflict resolution policy: last-write-wins (default), source-wins, destination-wins, skip",
            Required = false,
            DefaultValueFactory = _ => "last-write-wins",
        }.AcceptOnlyFromAmong("last-write-wins", "source-wins", "dest-wins", "destination-wins", "skip");

        var allOption = new Option<bool>("--all")
        {
            Description = "Display all matching contacts instead of the default first 100 shown",
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
            noDefaultSettingsOption,
            fromOption,
            filterOption,
            allOption,
            verboseOption,
        };
        contactsListCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string from = parseResult.GetValue(fromOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool all = parseResult.GetValue(allOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);
            var contactFilter = ContactFilter.Parse(filter);
            IReadOnlyList<CanonicalContact> contacts;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings))
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
            noDefaultSettingsOption,
            fromOption,
            toEndpointOption,
            filterOption,
            pruneOption,
            verboseOption,
        };
        contactsExportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool prune = parseResult.GetValue(pruneOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);
            var contactFilter = ContactFilter.Parse(filter);
            JobExecutionResult result;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings))
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
            noDefaultSettingsOption,
            fromOption,
            toEndpointOption,
            bidirectionalOption,
            reverseOption,
            filterOption,
            fullOption,
            forceOption,
            pruneOption,
            onConflictOption,
            confirmOption,
            whatIfOption,
            intervalOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(contactsSyncCommand, whatIfOption, confirmOption);
        AddMutuallyExclusiveBooleanOptionValidation(contactsSyncCommand, bidirectionalOption, reverseOption);

        // Validate that --interval is exclusive with --what-if and --confirm
        contactsSyncCommand.Validators.Add(parseResult =>
        {
            string? interval = parseResult.GetValue(intervalOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool confirm = parseResult.GetValue(confirmOption);

            if (interval is not null)
            {
                if (whatIf)
                {
                    parseResult.AddError("The --interval and --what-if options are mutually exclusive.");
                }

                if (confirm)
                {
                    parseResult.AddError("The --interval and --confirm options are mutually exclusive.");
                }
            }
        });

        contactsSyncCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
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
            bool verboseSettings = parseResult.GetValue(verboseOption);

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
                "destination-wins" => ConflictPolicy.DestinationWins,
                "skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.LastWriteWins,
            };

            var deletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings);
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
                    try
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
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        // Intentional shutdown via Ctrl+C or SIGTERM — exit cleanly.
                        break;
                    }
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
            noDefaultSettingsOption,
            fromOption,
            toEndpointOption,
            filterOption,
            forceOption,
            pruneOption,
            confirmOption,
            whatIfOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(contactsImportCommand, whatIfOption, confirmOption);
        contactsImportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            bool prune = parseResult.GetValue(pruneOption);
            string? filter = parseResult.GetValue(filterOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool confirm = parseResult.GetValue(confirmOption);
            bool force = parseResult.GetValue(forceOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);
            var contactFilter = ContactFilter.Parse(filter);
            JobExecutionResult result;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings))
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

    private static Command CreateEventsCommand()
    {
        // ── events command group ──────────────────────────────────────────────
        var settingsOption = new Option<string[]>("--settings")
        {
            Description = "Path to one or more settings JSON files to load",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        var noDefaultSettingsOption = new Option<bool>("--no-default-settings")
        {
            Description = "Skip loading embedded default settings",
            Arity = ArgumentArity.Zero,
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
            Description = "Destination endpoint name (as configured in the settings file)",
            Required = true,
        };

        var importSourceDirectoryOption = new Option<string>("--from")
        {
            Description = "Source directory path containing event JSON files to import",
            Required = true,
        };

        var exportOutputDirectoryOption = new Option<string>("--to")
        {
            Description = "Output directory path for exported event JSON files",
            Required = true,
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "OData-style filter expression, e.g. startswith(title,'A')",
            Required = false,
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Ignore cursors, bypass change and content-sameness checks, and write all in-scope events unconditionally (clobbers destination drift)",
            Arity = ArgumentArity.Zero,
        };

        var fullOption = new Option<bool>("--full")
        {
            Description = "Ignore saved cursors and fetch all rows from both sides, but still skip events whose content is already identical",
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
            Description = "Delete events on the destination that no longer exist on the source (or vice versa)",
            Arity = ArgumentArity.Zero,
        };

        var onConflictOption = new Option<string>("--on-conflict")
        {
            Description = "Conflict resolution policy: last-write-wins (default), source-wins, dest-wins, skip",
            Required = false,
            DefaultValueFactory = _ => "last-write-wins",
        }.AcceptOnlyFromAmong("last-write-wins", "source-wins", "dest-wins", "destination-wins", "skip");

        var allOption = new Option<bool>("--all")
        {
            Description = "Display all matching events instead of the default first 100 shown",
            Arity = ArgumentArity.Zero,
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable more detailed logging for this run",
            Arity = ArgumentArity.Zero,
        };

        // events list
        var eventsListCommand = new Command("list", "List events from a configured endpoint")
        {
            settingsOption,
            noDefaultSettingsOption,
            fromOption,
            filterOption,
            allOption,
            verboseOption,
        };
        eventsListCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string from = parseResult.GetValue(fromOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool all = parseResult.GetValue(allOption);
            bool verbose = parseResult.GetValue(verboseOption);
            var eventFilter = CalendarEventFilter.Parse(filter);
            IReadOnlyList<CanonicalEvent> events;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using (var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verbose))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalEvent>>();
                events = await handler.ListAsync(from, eventFilter, all ? null : 100, CancellationToken.None);
            }

            events = events
                .OrderBy(e => e.From)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (events.Count == 0)
            {
                Console.WriteLine("No events found.");
                return;
            }

            Console.WriteLine($"{"Title",-EventTitleColumnWidth} {"Start",-EventDateColumnWidth} {"End",-EventDateColumnWidth} {"Location",-EventLocationColumnWidth}");
            Console.WriteLine(new string('-', EventTitleColumnWidth + EventDateColumnWidth + EventDateColumnWidth + EventLocationColumnWidth + 3));
            foreach (var ev in events)
            {
                string start = ev.From == DateTimeOffset.MinValue ? string.Empty : ev.From.ToString("yyyy-MM-dd HH:mm");
                string end = ev.To == DateTimeOffset.MinValue ? string.Empty : ev.To.ToString("yyyy-MM-dd HH:mm");
                Console.WriteLine($"{FormatTableCell(ev.Title, EventTitleColumnWidth),-EventTitleColumnWidth} {start,-EventDateColumnWidth} {end,-EventDateColumnWidth} {FormatTableCell(ev.Location, EventLocationColumnWidth),-EventLocationColumnWidth}");
            }

            Console.WriteLine();
            Console.WriteLine(all
                ? $"Total: {events.Count} event(s)"
                : $"Showing {events.Count} event(s) (default limit: 100; use --all to display everything)");
        });

        // events export
        var eventsExportCommand = new Command("export", "Export events from an endpoint to local JSON files in a directory")
        {
            settingsOption,
            noDefaultSettingsOption,
            fromOption,
            exportOutputDirectoryOption,
            filterOption,
            pruneOption,
            verboseOption,
        };
        eventsExportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string from = parseResult.GetValue(fromOption)!;
            string to = parseResult.GetValue(exportOutputDirectoryOption)!;
            string? filter = parseResult.GetValue(filterOption);
            bool prune = parseResult.GetValue(pruneOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);
            var eventFilter = CalendarEventFilter.Parse(filter);
            JobExecutionResult result;

            await using (var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalEvent>>();
                result = await handler.ExportAsync(from, to, prune: prune, eventFilter, CancellationToken.None);
            }

            Console.WriteLine(result.Succeeded
                ? $"Export completed. Actions planned: {result.ActionsPlanned}"
                : $"Export failed: {result.Error}");
        });

        // events sync
        var intervalOption = new Option<string?>("--interval")
        {
            Description = "Repeat the sync indefinitely with the given delay between runs (ISO 8601 duration, e.g. PT15M). Without this option the command runs once and exits.",
            Required = false,
        };

        var eventsSyncCommand = new Command("sync", "Synchronize calendar events between two configured endpoints")
        {
            settingsOption,
            noDefaultSettingsOption,
            fromOption,
            toEndpointOption,
            bidirectionalOption,
            reverseOption,
            filterOption,
            fullOption,
            forceOption,
            pruneOption,
            onConflictOption,
            confirmOption,
            whatIfOption,
            intervalOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(eventsSyncCommand, whatIfOption, confirmOption);
        AddMutuallyExclusiveBooleanOptionValidation(eventsSyncCommand, bidirectionalOption, reverseOption);

        // Validate that --interval is exclusive with --what-if and --confirm
        eventsSyncCommand.Validators.Add(parseResult =>
        {
            string? interval = parseResult.GetValue(intervalOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool confirm = parseResult.GetValue(confirmOption);

            if (interval is not null)
            {
                if (whatIf)
                {
                    parseResult.AddError("The --interval and --what-if options are mutually exclusive.");
                }

                if (confirm)
                {
                    parseResult.AddError("The --interval and --confirm options are mutually exclusive.");
                }
            }
        });

        eventsSyncCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
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

            var eventFilter = CalendarEventFilter.Parse(filter);

            var conflictPolicy = onConflictStr.ToLowerInvariant() switch
            {
                "source-wins" => ConflictPolicy.SourceWins,
                "dest-wins" or "destination-wins" => ConflictPolicy.DestinationWins,
                "skip" => ConflictPolicy.Skip,
                _ => ConflictPolicy.LastWriteWins,
            };

            var deletePolicy = prune ? DeletePolicy.Mirror : DeletePolicy.Ignore;

            // Keep the provider alive while working so disposing it flushes the log factory before process exit.
            await using var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verbose);
            var handler = provider.GetRequiredService<CommandHandler<CanonicalEvent>>();

            if (interval.HasValue)
            {
                // Interval mode: repeat indefinitely; exit nonzero on failure.
                // Honor process-exit signals (SIGTERM from Docker, Ctrl+C) so the delay can be interrupted.
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await handler.SyncAsync(from, to, mode, whatIf, confirm, eventFilter, full, force, deletePolicy, conflictPolicy, cts.Token);

                        if (!result.Succeeded && result.Error is not null)
                        {
                            throw new InvalidOperationException($"Sync failed: {result.Error}");
                        }

                        Console.WriteLine(result.Succeeded
                            ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                            : $"Sync skipped: {result.SkipReason}");

                        await Task.Delay(interval.Value, cts.Token);
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        // Intentional shutdown via Ctrl+C or SIGTERM — exit cleanly.
                        break;
                    }
                }
            }
            else
            {
                // One-shot mode: run once and exit.
                var result = await handler.SyncAsync(from, to, mode, whatIf, confirm, eventFilter, full, force, deletePolicy, conflictPolicy, CancellationToken.None);

                Console.WriteLine(result.Succeeded
                    ? $"Sync completed. Actions planned: {result.ActionsPlanned}"
                    : result.Error is not null
                        ? $"Sync failed: {result.Error}"
                        : $"Sync skipped: {result.SkipReason}");
            }
        });

        var eventsImportCommand = new Command("import", "Import events from local JSON files in a directory into a configured endpoint")
        {
            settingsOption,
            noDefaultSettingsOption,
            importSourceDirectoryOption,
            toEndpointOption,
            filterOption,
            forceOption,
            pruneOption,
            confirmOption,
            whatIfOption,
            verboseOption,
        };
        AddMutuallyExclusiveBooleanOptionValidation(eventsImportCommand, whatIfOption, confirmOption);
        eventsImportCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string from = parseResult.GetValue(importSourceDirectoryOption)!;
            string to = parseResult.GetValue(toEndpointOption)!;
            bool prune = parseResult.GetValue(pruneOption);
            string? filter = parseResult.GetValue(filterOption);
            bool whatIf = parseResult.GetValue(whatIfOption);
            bool confirm = parseResult.GetValue(confirmOption);
            bool force = parseResult.GetValue(forceOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);
            var eventFilter = CalendarEventFilter.Parse(filter);
            JobExecutionResult result;

            await using (var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings))
            {
                var handler = provider.GetRequiredService<CommandHandler<CanonicalEvent>>();
                result = await handler.ImportAsync(from, to, prune, eventFilter, whatIf, confirm, force, CancellationToken.None);
            }

            Console.WriteLine(result.Succeeded
                ? $"Import completed. Actions planned: {result.ActionsPlanned}"
                : $"Import failed: {result.Error ?? result.SkipReason}");
        });

        var eventsCommand = new Command("events", "Interactive calendar event operations")
        {
            eventsListCommand,
            eventsExportCommand,
            eventsImportCommand,
            eventsSyncCommand,
        };

        return eventsCommand;
    }

    private static Command CreateJobsCommand()
    {
        // ── jobs command group ────────────────────────────────────────────────
        var settingsOption = new Option<string[]>("--settings")
        {
            Description = "Path to one or more settings JSON files to load",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        var noDefaultSettingsOption = new Option<bool>("--no-default-settings")
        {
            Description = "Skip loading embedded default settings",
            Arity = ArgumentArity.Zero,
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Enable more detailed logging for this run",
            Arity = ArgumentArity.Zero,
        };

        // jobs list
        var jobsListCommand = new Command("list", "List all known jobs and their current lock state")
        {
            settingsOption,
            noDefaultSettingsOption,
            verboseOption,
        };
        jobsListCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);

            await using var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings);
            var executor = provider.GetRequiredService<JobExecutor>();
            var jobs = await executor.ListJobsAsync(CancellationToken.None);

            if (jobs.Count == 0)
            {
                Console.WriteLine("No jobs found. Run a sync first.");
                return;
            }

            Console.WriteLine($"{"Key",-45} {"Type",-10} {"From",-20} {"To",-20} {"Locked"}");
            Console.WriteLine(new string('-', 102));
            foreach (var job in jobs)
            {
                (string type, string from, string to) = ParseJobKey(job.JobKey);
                Console.WriteLine($"{job.JobKey,-45} {type,-10} {from,-20} {to,-20} {(job.Locked ? "yes" : "no")}");
            }
        });

        // jobs reset
        var resetKeyOption = new Option<string?>("--key")
        {
            Description = "Canonical job key to reset (e.g. contacts:Microsoft:Google or events:Work:Archive)",
            Required = false,
        };

        var resetAllOption = new Option<bool>("--all")
        {
            Description = "Reset sync state for all jobs",
            Arity = ArgumentArity.Zero,
        };

        var jobsResetCommand = new Command("reset", "Reset stored sync state (link state, cursors, and locks) for a job or all jobs")
        {
            settingsOption,
            noDefaultSettingsOption,
            resetKeyOption,
            resetAllOption,
            verboseOption,
        };
        jobsResetCommand.Validators.Add(parseResult =>
        {
            bool hasKey = parseResult.GetValue(resetKeyOption) is not null;
            bool hasAll = parseResult.GetValue(resetAllOption);

            if (!hasAll && !hasKey)
            {
                parseResult.AddError("Specify --key <jobKey> to reset a specific job, or --all to reset everything.");
            }

            if (hasAll && hasKey)
            {
                parseResult.AddError("The --all option is mutually exclusive with --key.");
            }
        });
        jobsResetCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string? key = parseResult.GetValue(resetKeyOption);
            bool resetAll = parseResult.GetValue(resetAllOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);

            await using var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings);
            var executor = provider.GetRequiredService<JobExecutor>();

            if (resetAll)
            {
                await executor.ResetAllAsync(CancellationToken.None);
                Console.WriteLine("Reset all: sync state cleared.");
            }
            else
            {
                await executor.ResetJobAsync(key!, CancellationToken.None);
                Console.WriteLine($"Reset job {key}.");
            }
        });

        // jobs unlock
        var unlockKeyOption = new Option<string?>("--key")
        {
            Description = "Canonical job key to unlock (e.g. contacts:Microsoft:Google or events:Work:Archive)",
            Required = false,
        };

        var unlockAllOption = new Option<bool>("--all")
        {
            Description = "Force-release locks for all jobs",
            Arity = ArgumentArity.Zero,
        };

        var jobsUnlockCommand = new Command("unlock", "Force-release job locks (use after a crash to clear stuck locks)")
        {
            settingsOption,
            noDefaultSettingsOption,
            unlockKeyOption,
            unlockAllOption,
            verboseOption,
        };
        jobsUnlockCommand.Validators.Add(parseResult =>
        {
            bool hasKey = parseResult.GetValue(unlockKeyOption) is not null;
            bool hasAll = parseResult.GetValue(unlockAllOption);

            if (!hasAll && !hasKey)
            {
                parseResult.AddError("Specify --key <jobKey> to unlock a specific job, or --all to unlock all jobs.");
            }

            if (hasAll && hasKey)
            {
                parseResult.AddError("The --all option is mutually exclusive with --key.");
            }
        });
        jobsUnlockCommand.SetAction(async parseResult =>
        {
            string[] settingsFiles = parseResult.GetValue(settingsOption) ?? [];
            bool noDefaultSettings = parseResult.GetValue(noDefaultSettingsOption);
            string? key = parseResult.GetValue(unlockKeyOption);
            bool unlockAll = parseResult.GetValue(unlockAllOption);
            bool verboseSettings = parseResult.GetValue(verboseOption);

            await using var provider = BuildServiceProvider(settingsFiles, noDefaultSettings, verboseSettings);
            var executor = provider.GetRequiredService<JobExecutor>();

            if (unlockAll)
            {
                int cleared = await executor.UnlockAllJobsAsync(CancellationToken.None);
                Console.WriteLine(cleared > 0
                    ? $"Cleared {cleared} job lock(s)."
                    : "No locks found.");
            }
            else
            {
                int cleared = await executor.UnlockJobAsync(key!, CancellationToken.None);
                Console.WriteLine(cleared > 0
                    ? $"Cleared lock for job {key}."
                    : $"No lock found for job {key}.");
            }
        });

        var jobsCommand = new Command("jobs", "Inspect and manage persisted job state")
        {
            jobsListCommand,
            jobsResetCommand,
            jobsUnlockCommand,
        };

        return jobsCommand;
    }

    private static ServiceProvider BuildServiceProvider(string[] settingsFiles, bool noDefaultSettings, bool verboseSettings)
    {
        var configBuilder = new ConfigurationBuilder();

        configBuilder.AddKagamiSettings(noDefaultSettings, noApplicationDataSettings: false, settingsFiles, verboseSettings);
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
    /// Splits a canonical job key (<c>type:from:to</c>) into its constituent parts.
    /// Returns an empty string for any part that is missing.
    /// </summary>
    private static (string Type, string From, string To) ParseJobKey(string jobKey)
    {
        string[] parts = jobKey.Split(':', 3);
        return (
            parts.Length >= 1 ? parts[0] : string.Empty,
            parts.Length >= 2 ? parts[1] : string.Empty,
            parts.Length >= 3 ? parts[2] : string.Empty);
    }

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

    /// <summary>
    /// Formats a table cell by truncating long values so fixed-width console columns stay aligned.
    /// </summary>
    internal static string FormatTableCell(string? value, int width)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Column width must be greater than zero.");
        }

        string text = value ?? string.Empty;
        if (text.Length <= width)
        {
            return text;
        }

        if (width <= 3)
        {
            return text[..width];
        }

        return text[..(width - 3)] + "...";
    }
}
