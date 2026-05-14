namespace Summerdawn.Kagami.Tests;

public sealed class ProgramTests
{
    public static TheoryData<string[]> VerboseCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "list", "--from", "Microsoft", "--verbose"]);
            data.Add(["contacts", "export", "--from", "Microsoft", "--to", "/tmp/out", "--verbose"]);
            data.Add(["contacts", "import", "--from", "/tmp/in", "--to", "Google", "--verbose"]);
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--verbose"]);
            data.Add(["events", "list", "--from", "Microsoft", "--verbose"]);
            data.Add(["events", "export", "--from", "Microsoft", "--to", "/tmp/out", "--verbose"]);
            data.Add(["events", "import", "--from", "/tmp/in", "--to", "Google", "--verbose"]);
            data.Add(["events", "sync", "--from", "Microsoft", "--to", "Google", "--verbose"]);
            data.Add(["jobs", "list", "--verbose"]);
            data.Add(["jobs", "reset", "--all", "--verbose"]);
            data.Add(["jobs", "unlock", "--all", "--verbose"]);
            return data;
        }
    }

    public static TheoryData<string[]> ConfirmationModeCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--confirm"]);
            data.Add(["contacts", "import", "--from", "/tmp/in", "--to", "Google", "--confirm"]);
            data.Add(["events", "import", "--from", "/tmp/in", "--to", "Google", "--confirm"]);
            return data;
        }
    }

    public static TheoryData<string[]> ReverseModeCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--reverse"]);
            return data;
        }
    }

    public static TheoryData<string[]> MutuallyExclusiveExecutionModeCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--what-if", "--confirm"]);
            data.Add(["contacts", "import", "--from", "/tmp/in", "--to", "Google", "--what-if", "--confirm"]);
            data.Add(["events", "import", "--from", "/tmp/in", "--to", "Google", "--what-if", "--confirm"]);
            return data;
        }
    }

    public static TheoryData<string[]> MutuallyExclusiveContactsSyncDirectionCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--bidirectional", "--reverse"]);
            return data;
        }
    }

    public static TheoryData<string[]> ValidIntervalSyncCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--interval", "PT15M"]);
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--interval", "PT2H"]);
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--interval", "PT30S"]);
            return data;
        }
    }

    public static TheoryData<string[]> MutuallyExclusiveIntervalExecutionModeCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--interval", "PT15M", "--what-if"]);
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--interval", "PT15M", "--confirm"]);
            return data;
        }
    }

    public static TheoryData<string[]> ValidJobsResetCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["jobs", "reset", "--key", "contacts:Microsoft:Google"]);
            data.Add(["jobs", "reset", "--all"]);
            return data;
        }
    }

    public static TheoryData<string[]> InvalidJobsResetCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            // Neither --all nor --key
            data.Add(["jobs", "reset"]);
            // --all combined with --key
            data.Add(["jobs", "reset", "--all", "--key", "contacts:Microsoft:Google"]);
            return data;
        }
    }

    public static TheoryData<string[]> ValidJobsUnlockCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["jobs", "unlock", "--key", "contacts:Microsoft:Google"]);
            data.Add(["jobs", "unlock", "--all"]);
            return data;
        }
    }

    public static TheoryData<string[]> InvalidJobsUnlockCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            // Neither --all nor --key
            data.Add(["jobs", "unlock"]);
            // --all combined with --key
            data.Add(["jobs", "unlock", "--all", "--key", "contacts:Microsoft:Google"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ConfirmationModeCommands))]
    public void CreateRootCommand_AllowsConfirmWithoutWhatIf(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(ReverseModeCommands))]
    public void CreateRootCommand_AcceptsReverseOnContactsSync(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(MutuallyExclusiveExecutionModeCommands))]
    public void CreateRootCommand_RejectsWhatIfAndConfirmTogether(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Contains(parseResult.Errors, error => error.Message.Contains("mutually exclusive", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MutuallyExclusiveContactsSyncDirectionCommands))]
    public void CreateRootCommand_RejectsReverseAndBidirectionalTogether(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Contains(parseResult.Errors, error => error.Message.Contains("mutually exclusive", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(VerboseCommands))]
    public void CreateRootCommand_AcceptsVerboseOnRelevantSubcommands(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(ValidIntervalSyncCommands))]
    public void CreateRootCommand_AcceptsIntervalOnContactsSync(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(MutuallyExclusiveIntervalExecutionModeCommands))]
    public void CreateRootCommand_RejectsIntervalWithWhatIfOrConfirm(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Contains(parseResult.Errors, error => error.Message.Contains("mutually exclusive", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ValidJobsResetCommands))]
    public void CreateRootCommand_AcceptsValidJobsResetArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(InvalidJobsResetCommands))]
    public void CreateRootCommand_RejectsInvalidJobsResetArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.NotEmpty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(ValidJobsUnlockCommands))]
    public void CreateRootCommand_AcceptsValidJobsUnlockArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(InvalidJobsUnlockCommands))]
    public void CreateRootCommand_RejectsInvalidJobsUnlockArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void CreateRootCommand_ExposesJobsCommand()
    {
        var rootCommand = Program.CreateRootCommand();

        Assert.Contains(rootCommand.Subcommands, cmd => cmd.Name == "jobs");
    }

    [Fact]
    public void CreateRootCommand_JobsCommandHasListResetUnlock()
    {
        var rootCommand = Program.CreateRootCommand();
        var jobsCommand = rootCommand.Subcommands.First(cmd => cmd.Name == "jobs");

        Assert.Contains(jobsCommand.Subcommands, cmd => cmd.Name == "list");
        Assert.Contains(jobsCommand.Subcommands, cmd => cmd.Name == "reset");
        Assert.Contains(jobsCommand.Subcommands, cmd => cmd.Name == "unlock");
    }

    [Fact]
    public void CreateRootCommand_ContactsCommandDoesNotHaveResetOrUnlock()
    {
        var rootCommand = Program.CreateRootCommand();
        var contactsCommand = rootCommand.Subcommands.First(cmd => cmd.Name == "contacts");

        Assert.DoesNotContain(contactsCommand.Subcommands, cmd => cmd.Name == "reset");
        Assert.DoesNotContain(contactsCommand.Subcommands, cmd => cmd.Name == "unlock");
    }

    [Fact]
    public void ParseIntervalArgument_ParsesMinutes()
    {
        var result = Program.ParseIntervalArgument("PT15M");

        Assert.Equal(TimeSpan.FromMinutes(15), result);
    }

    [Fact]
    public void ParseIntervalArgument_ParsesHours()
    {
        var result = Program.ParseIntervalArgument("PT2H");

        Assert.Equal(TimeSpan.FromHours(2), result);
    }

    [Fact]
    public void ParseIntervalArgument_ParsesSeconds()
    {
        var result = Program.ParseIntervalArgument("PT30S");

        Assert.Equal(TimeSpan.FromSeconds(30), result);
    }

    [Fact]
    public void ParseIntervalArgument_ThrowsOnInvalidFormat()
    {
        Assert.Throws<FormatException>(() => Program.ParseIntervalArgument("not-a-duration"));
    }

    [Fact]
    public void ParseIntervalArgument_ThrowsOnUnsupportedUnit()
    {
        Assert.Throws<FormatException>(() => Program.ParseIntervalArgument("P1D"));
    }

    [Fact]
    public void FormatTableCell_LeavesShortValuesUnchanged()
    {
        string result = Program.FormatTableCell("Short title", 40);

        Assert.Equal("Short title", result);
    }

    [Fact]
    public void FormatTableCell_TruncatesLongValuesWithEllipsis()
    {
        string result = Program.FormatTableCell("This is a very long event title that should not overflow", 20);

        Assert.Equal("This is a very lo...", result);
        Assert.Equal(20, result.Length);
    }

    public static TheoryData<string[]> ValidEventsSyncCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar"]);
            data.Add(["events", "export", "--from", "MicrosoftCalendar", "--to", "/tmp/out"]);
            data.Add(["events", "import", "--from", "/tmp/in", "--to", "GoogleCalendar"]);
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar", "--bidirectional"]);
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar", "--reverse"]);
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar", "--what-if"]);
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar", "--on-conflict", "dest-wins"]);
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar", "--interval", "PT15M"]);
            return data;
        }
    }

    public static TheoryData<string[]> DestWinsCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--on-conflict", "dest-wins"]);
            data.Add(["events", "sync", "--from", "MicrosoftCalendar", "--to", "GoogleCalendar", "--on-conflict", "dest-wins"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidEventsSyncCommands))]
    public void CreateRootCommand_AcceptsValidEventsSyncArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(DestWinsCommands))]
    public void CreateRootCommand_AcceptsDestWinsAlias(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void CreateRootCommand_ExposesEventsCommand()
    {
        var rootCommand = Program.CreateRootCommand();

        Assert.Contains(rootCommand.Subcommands, cmd => cmd.Name == "events");
    }

    [Fact]
    public void CreateRootCommand_EventsCommandHasListImportExportAndSync()
    {
        var rootCommand = Program.CreateRootCommand();
        var eventsCommand = rootCommand.Subcommands.First(cmd => cmd.Name == "events");

        Assert.Contains(eventsCommand.Subcommands, cmd => cmd.Name == "list");
        Assert.Contains(eventsCommand.Subcommands, cmd => cmd.Name == "export");
        Assert.Contains(eventsCommand.Subcommands, cmd => cmd.Name == "import");
        Assert.Contains(eventsCommand.Subcommands, cmd => cmd.Name == "sync");
    }
}
