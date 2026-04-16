namespace Summerdawn.Kagami.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>
/// Initializes and manages the internal Kagami SQLite state database.
/// </summary>
public sealed class StateDatabase(string databasePath, ILogger<StateDatabase> logger)
{
    private readonly string connectionString = $"Data Source={databasePath}";

    /// <summary>Gets a new open connection to the state database.</summary>
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
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            job_key          TEXT NOT NULL,
            entity_type      TEXT NOT NULL,
            side_a_id        TEXT NOT NULL,
            side_b_id        TEXT,
            side_a_version   TEXT,
            side_b_version   TEXT,
            side_a_hash      TEXT,
            side_b_hash      TEXT,
            side_a_deleted   INTEGER NOT NULL DEFAULT 0,
            side_b_deleted   INTEGER NOT NULL DEFAULT 0,
            side_a_last_seen TEXT,
            side_b_last_seen TEXT,
            origin_side      TEXT,
            last_synced_at   TEXT,
            last_sync_result TEXT,
            conflict_state   TEXT,
            UNIQUE (job_key, side_a_id)
        );

        CREATE INDEX IF NOT EXISTS idx_link_state_job_key ON link_state (job_key);
        CREATE INDEX IF NOT EXISTS idx_link_state_side_b_id ON link_state (job_key, side_b_id);

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
