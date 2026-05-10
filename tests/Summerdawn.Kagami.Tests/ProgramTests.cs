namespace Summerdawn.Kagami.Tests;

public sealed class ProgramTests
{
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

    [Theory]
    [MemberData(nameof(MutuallyExclusiveExecutionModeCommands))]
    public void CreateRootCommand_RejectsWhatIfAndConfirmTogether(string[] args)
    {
        var parseResult = Program.CreateRootCommand().Parse(args);

        Assert.Contains(parseResult.Errors, error => error.Message.Contains("mutually exclusive", StringComparison.Ordinal));
    }
}
