namespace Summerdawn.Kagami.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>
/// CRUD operations for link-state rows, partitioned by job key.
/// </summary>
public sealed class LinkStateRepository(StateDatabase db)
{
    /// <summary>Returns all link rows for a given job key.</summary>
    public async Task<IReadOnlyList<LinkStateRow>> GetByJobAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM link_state WHERE job_key = @jobKey";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        return await ReadRowsAsync(cmd, cancellationToken);
    }

    /// <summary>Returns the link row matching a source item ID within a job.</summary>
    public async Task<LinkStateRow?> GetBySourceIdAsync(string jobKey, string sourceId, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM link_state WHERE job_key = @jobKey AND source_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@id", sourceId);
        var rows = await ReadRowsAsync(cmd, cancellationToken);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Returns the link row matching a destination item ID within a job.</summary>
    public async Task<LinkStateRow?> GetByDestinationIdAsync(string jobKey, string destinationId, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM link_state WHERE job_key = @jobKey AND destination_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@id", destinationId);
        var rows = await ReadRowsAsync(cmd, cancellationToken);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Upserts a link-state row.</summary>
    public async Task UpsertAsync(LinkStateRow row, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO link_state (
                job_key, entity_type, source_id, destination_id,
                source_version, destination_version, source_hash, destination_hash,
                source_deleted, destination_deleted,
                source_last_seen, destination_last_seen,
                origin_side, last_synced_at, last_sync_result, conflict_state
            ) VALUES (
                @jobKey, @entityType, @sourceId, @destinationId,
                @sourceVersion, @destinationVersion, @sourceHash, @destinationHash,
                @sourceDeleted, @destinationDeleted,
                @sourceLastSeen, @destinationLastSeen,
                @originSide, @lastSyncedAt, @lastSyncResult, @conflictState
            )
            ON CONFLICT(job_key, source_id) DO UPDATE SET
                destination_id       = excluded.destination_id,
                source_version       = excluded.source_version,
                destination_version  = excluded.destination_version,
                source_hash          = excluded.source_hash,
                destination_hash     = excluded.destination_hash,
                source_deleted       = excluded.source_deleted,
                destination_deleted  = excluded.destination_deleted,
                source_last_seen     = excluded.source_last_seen,
                destination_last_seen = excluded.destination_last_seen,
                origin_side          = excluded.origin_side,
                last_synced_at       = excluded.last_synced_at,
                last_sync_result     = excluded.last_sync_result,
                conflict_state       = excluded.conflict_state
            """;

        cmd.Parameters.AddWithValue("@jobKey", row.JobKey);
        cmd.Parameters.AddWithValue("@entityType", row.EntityType);
        cmd.Parameters.AddWithValue("@sourceId", row.SourceId);
        cmd.Parameters.AddWithValue("@destinationId", (object?)row.DestinationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceVersion", (object?)row.SourceVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destinationVersion", (object?)row.DestinationVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceHash", (object?)row.SourceHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destinationHash", (object?)row.DestinationHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceDeleted", row.SourceDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@destinationDeleted", row.DestinationDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@sourceLastSeen", row.SourceLastSeen?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@destinationLastSeen", row.DestinationLastSeen?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@originSide", (object?)row.OriginSide ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastSyncedAt", row.LastSyncedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@lastSyncResult", (object?)row.LastSyncResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conflictState", (object?)row.ConflictState ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes all link rows for a job (reset).</summary>
    public async Task DeleteByJobAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM link_state WHERE job_key = @jobKey";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<LinkStateRow>> ReadRowsAsync(SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var rows = new List<LinkStateRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LinkStateRow
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                JobKey = reader.GetString(reader.GetOrdinal("job_key")),
                EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
                SourceId = reader.GetString(reader.GetOrdinal("source_id")),
                DestinationId = reader.IsDBNull(reader.GetOrdinal("destination_id")) ? null : reader.GetString(reader.GetOrdinal("destination_id")),
                SourceVersion = reader.IsDBNull(reader.GetOrdinal("source_version")) ? null : reader.GetString(reader.GetOrdinal("source_version")),
                DestinationVersion = reader.IsDBNull(reader.GetOrdinal("destination_version")) ? null : reader.GetString(reader.GetOrdinal("destination_version")),
                SourceHash = reader.IsDBNull(reader.GetOrdinal("source_hash")) ? null : reader.GetString(reader.GetOrdinal("source_hash")),
                DestinationHash = reader.IsDBNull(reader.GetOrdinal("destination_hash")) ? null : reader.GetString(reader.GetOrdinal("destination_hash")),
                SourceDeleted = reader.GetInt32(reader.GetOrdinal("source_deleted")) != 0,
                DestinationDeleted = reader.GetInt32(reader.GetOrdinal("destination_deleted")) != 0,
                SourceLastSeen = reader.IsDBNull(reader.GetOrdinal("source_last_seen")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("source_last_seen"))),
                DestinationLastSeen = reader.IsDBNull(reader.GetOrdinal("destination_last_seen")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("destination_last_seen"))),
                OriginSide = reader.IsDBNull(reader.GetOrdinal("origin_side")) ? null : reader.GetString(reader.GetOrdinal("origin_side")),
                LastSyncedAt = reader.IsDBNull(reader.GetOrdinal("last_synced_at")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_synced_at"))),
                LastSyncResult = reader.IsDBNull(reader.GetOrdinal("last_sync_result")) ? null : reader.GetString(reader.GetOrdinal("last_sync_result")),
                ConflictState = reader.IsDBNull(reader.GetOrdinal("conflict_state")) ? null : reader.GetString(reader.GetOrdinal("conflict_state")),
            });
        }

        return rows;
    }
}
