using Microsoft.Data.Sqlite;

namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Manages the internal Kagami SQLite state database, including schema initialization.
/// </summary>
public sealed class StateDatabase(string databasePath, ILogger<StateDatabase> logger)
{
    private readonly string connectionString = $"Data Source={databasePath}";

    /// <summary>
    /// Opens and returns a new connection to the state database.
    /// </summary>
    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Initializes the database schema, creating tables if they do not yet exist,
    /// and applies any pending schema migrations.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Initializing state database at {Path}", databasePath);

        await using var conn = OpenConnection();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaScript;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await MigrateJobLeasesAsync(conn, cancellationToken);

        logger.LogDebug("State database initialized");
    }

    /// <summary>
    /// Migrates <c>job_leases</c> from the old insert/delete schema
    /// (with <c>holder_id</c> and <c>expires_at</c>) to the simplified
    /// one-row-per-job schema with a <c>locked</c> boolean column.
    /// No-ops when the table is already on the new schema.
    /// </summary>
    private static async Task MigrateJobLeasesAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        // Detect old schema by checking for the 'holder_id' column.
        bool hasOldSchema = false;
        await using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(job_leases)";
            await using var reader = await pragmaCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(
                        reader.GetString(reader.GetOrdinal("name")),
                        "holder_id",
                        StringComparison.Ordinal))
                {
                    hasOldSchema = true;
                    break;
                }
            }
        }

        if (!hasOldSchema)
        {
            return;
        }

        // Migrate: recreate job_leases with the simplified schema, preserving active leases as locked=1.
        await using var migrateCmd = conn.CreateCommand();
        migrateCmd.CommandText = """
            CREATE TABLE job_leases_new (
                job_key TEXT NOT NULL PRIMARY KEY,
                locked  INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO job_leases_new (job_key, locked)
                SELECT job_key,
                       CASE WHEN expires_at > strftime('%Y-%m-%dT%H:%M:%fZ','now') THEN 1 ELSE 0 END
                FROM job_leases;
            DROP TABLE job_leases;
            ALTER TABLE job_leases_new RENAME TO job_leases;
            """;
        await migrateCmd.ExecuteNonQueryAsync(cancellationToken);
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
            partition_key        TEXT NOT NULL,
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
            UNIQUE (partition_key, source_id)
        );

        CREATE INDEX IF NOT EXISTS idx_link_state_partition_key ON link_state (partition_key);
        CREATE INDEX IF NOT EXISTS idx_link_state_destination_id ON link_state (partition_key, destination_id);

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
            job_key TEXT NOT NULL PRIMARY KEY,
            locked  INTEGER NOT NULL DEFAULT 0
        );
        """;
}
