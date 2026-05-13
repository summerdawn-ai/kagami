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
            data.Add(["contacts", "reset", "--all", "--verbose"]);
            data.Add(["contacts", "unlock", "--all", "--verbose"]);
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

    public static TheoryData<string[]> ValidResetCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "reset", "--from", "Microsoft", "--to", "Google"]);
            data.Add(["contacts", "reset", "--all"]);
            return data;
        }
    }

    public static TheoryData<string[]> InvalidResetCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            // Neither --all nor --from+--to
            data.Add(["contacts", "reset"]);
            // Only --from without --to
            data.Add(["contacts", "reset", "--from", "Microsoft"]);
            // Only --to without --from
            data.Add(["contacts", "reset", "--to", "Google"]);
            // --all combined with --from
            data.Add(["contacts", "reset", "--all", "--from", "Microsoft", "--to", "Google"]);
            return data;
        }
    }

    public static TheoryData<string[]> ValidUnlockCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["contacts", "unlock", "--from", "Microsoft", "--to", "Google"]);
            data.Add(["contacts", "unlock", "--all"]);
            return data;
        }
    }

    public static TheoryData<string[]> InvalidUnlockCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            // Neither --all nor --from+--to
            data.Add(["contacts", "unlock"]);
            // Only --from without --to
            data.Add(["contacts", "unlock", "--from", "Microsoft"]);
            // --all combined with --from
            data.Add(["contacts", "unlock", "--all", "--from", "Microsoft", "--to", "Google"]);
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
    [MemberData(nameof(ValidResetCommands))]
    public void CreateRootCommand_AcceptsValidContactsResetArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(InvalidResetCommands))]
    public void CreateRootCommand_RejectsInvalidContactsResetArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.NotEmpty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(ValidUnlockCommands))]
    public void CreateRootCommand_AcceptsValidContactsUnlockArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Empty(parseResult.Errors);
    }

    [Theory]
    [MemberData(nameof(InvalidUnlockCommands))]
    public void CreateRootCommand_RejectsInvalidContactsUnlockArgs(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void CreateRootCommand_DoesNotExposeJobsCommand()
    {
        var rootCommand = Program.CreateRootCommand();

        Assert.DoesNotContain(rootCommand.Subcommands, cmd => cmd.Name == "jobs");
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
}
