namespace Summerdawn.Kagami.Tests.TestSupport;

public sealed class TestDatabasePath : IDisposable
{
    public TestDatabasePath()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kagami-tests",
            $"{Guid.NewGuid():N}.db");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
