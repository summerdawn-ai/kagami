namespace Summerdawn.Kagami.Tests;

public sealed class ProgramTests
{
    public static TheoryData<string[]> VerboseCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["jobs", "list", "--verbose"]);
            data.Add(["jobs", "run", "--verbose"]);
            data.Add(["jobs", "reset", "--all", "--verbose"]);
            data.Add(["jobs", "unlock", "--verbose"]);
            data.Add(["contacts", "list", "--from", "Microsoft", "--verbose"]);
            data.Add(["contacts", "export", "--from", "Microsoft", "--to", "/tmp/out", "--verbose"]);
            data.Add(["contacts", "import", "--from", "/tmp/in", "--to", "Google", "--verbose"]);
            data.Add(["contacts", "sync", "--from", "Microsoft", "--to", "Google", "--verbose"]);
            return data;
        }
    }

    public static TheoryData<string[]> ConfirmationModeCommands
    {
        get
        {
            TheoryData<string[]> data = [];
            data.Add(["jobs", "run", "--confirm"]);
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
            data.Add(["jobs", "run", "--what-if", "--confirm"]);
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

    [Fact]
    public void CreateRootCommand_RejectsVerboseBeforeFirstCommand()
    {
        var parseResult = Program.CreateRootCommand().Parse(["--verbose", "jobs", "list"]);

        Assert.NotEmpty(parseResult.Errors);
    }
}
