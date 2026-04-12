namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Internal SQLite state database settings.
/// </summary>
public sealed class PersistenceOptions
{
    /// <summary>
    /// Path to the internal SQLite state database file.
    /// </summary>
    public string DatabasePath { get; set; } = "kagami-state.db";
}
