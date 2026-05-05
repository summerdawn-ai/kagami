using Microsoft.Data.Sqlite;

namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Initializes and manages the internal Kagami SQLite state database.
/// </summary>
public sealed class StateDatabase(string databasePath, ILogger<StateDatabase> logger)
{
    private readonly string connectionString = $"Data Source={databasePath}";

    /// <summary>
    /// Opens and returns a new SQLite connection to the state database.
    /// </summary>
    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Ensures the schema exists, creating tables if necessary.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Initializing state database at {Path}", databasePath);

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaScript;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("State database initialized");
    }

    private const string SchemaScript = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS endpoint_cursors (
            endpoint_name TEXT NOT NULL,
            cursor_value  TEXT NOT NULL,
            updated_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            PRIMARY KEY (endpoint_name)
        );

        CREATE TABLE IF NOT EXISTS job_endpoint_cursors (
            job_key       TEXT NOT NULL,
            endpoint_name TEXT NOT NULL,
            scope         TEXT NOT NULL DEFAULT '',
            cursor_value  TEXT NOT NULL,
            updated_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            PRIMARY KEY (job_key, endpoint_name)
        );

        CREATE TABLE IF NOT EXISTS link_state (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            job_key              TEXT NOT NULL,
            entity_type          TEXT NOT NULL,
            source_id            TEXT NOT NULL,
            destination_id       TEXT,
            source_version       TEXT,
            destination_version  TEXT,
            source_hash          TEXT,
            destination_hash     TEXT,
            source_deleted       INTEGER NOT NULL DEFAULT 0,
            destination_deleted  INTEGER NOT NULL DEFAULT 0,
            source_last_seen     TEXT,
            destination_last_seen TEXT,
            origin_side          TEXT,
            last_synced_at       TEXT,
            last_sync_result     TEXT,
            conflict_state       TEXT,
            UNIQUE (job_key, source_id)
        );

        CREATE INDEX IF NOT EXISTS idx_link_state_job_key ON link_state (job_key);
        CREATE INDEX IF NOT EXISTS idx_link_state_destination_id ON link_state (job_key, destination_id);

        CREATE TABLE IF NOT EXISTS operation_log (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            job_key      TEXT NOT NULL,
            entity_type  TEXT NOT NULL,
            operation    TEXT NOT NULL,
            item_id      TEXT NOT NULL,
            side         TEXT NOT NULL,
            result       TEXT NOT NULL,
            detail       TEXT,
            occurred_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
        );

        CREATE TABLE IF NOT EXISTS job_leases (
            job_key     TEXT NOT NULL PRIMARY KEY,
            holder_id   TEXT NOT NULL,
            acquired_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
            expires_at  TEXT NOT NULL
        );
        """;
}
