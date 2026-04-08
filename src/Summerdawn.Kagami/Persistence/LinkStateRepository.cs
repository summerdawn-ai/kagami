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

    /// <summary>Returns the link row matching a side-A item ID within a job.</summary>
    public async Task<LinkStateRow?> GetBySideAIdAsync(string jobKey, string sideAId, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM link_state WHERE job_key = @jobKey AND side_a_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@id", sideAId);
        var rows = await ReadRowsAsync(cmd, cancellationToken);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Returns the link row matching a side-B item ID within a job.</summary>
    public async Task<LinkStateRow?> GetBySideBIdAsync(string jobKey, string sideBId, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM link_state WHERE job_key = @jobKey AND side_b_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@id", sideBId);
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
                job_key, entity_type, side_a_id, side_b_id,
                side_a_version, side_b_version, side_a_hash, side_b_hash,
                side_a_deleted, side_b_deleted,
                side_a_last_seen, side_b_last_seen,
                origin_side, last_synced_at, last_sync_result, conflict_state
            ) VALUES (
                @jobKey, @entityType, @sideAId, @sideBId,
                @sideAVersion, @sideBVersion, @sideAHash, @sideBHash,
                @sideADeleted, @sideBDeleted,
                @sideALastSeen, @sideBLastSeen,
                @originSide, @lastSyncedAt, @lastSyncResult, @conflictState
            )
            ON CONFLICT(job_key, side_a_id) DO UPDATE SET
                side_b_id        = excluded.side_b_id,
                side_a_version   = excluded.side_a_version,
                side_b_version   = excluded.side_b_version,
                side_a_hash      = excluded.side_a_hash,
                side_b_hash      = excluded.side_b_hash,
                side_a_deleted   = excluded.side_a_deleted,
                side_b_deleted   = excluded.side_b_deleted,
                side_a_last_seen = excluded.side_a_last_seen,
                side_b_last_seen = excluded.side_b_last_seen,
                origin_side      = excluded.origin_side,
                last_synced_at   = excluded.last_synced_at,
                last_sync_result = excluded.last_sync_result,
                conflict_state   = excluded.conflict_state
            """;

        cmd.Parameters.AddWithValue("@jobKey", row.JobKey);
        cmd.Parameters.AddWithValue("@entityType", row.EntityType);
        cmd.Parameters.AddWithValue("@sideAId", row.SideAId);
        cmd.Parameters.AddWithValue("@sideBId", (object?)row.SideBId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sideAVersion", (object?)row.SideAVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sideBVersion", (object?)row.SideBVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sideAHash", (object?)row.SideAHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sideBHash", (object?)row.SideBHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sideADeleted", row.SideADeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@sideBDeleted", row.SideBDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@sideALastSeen", row.SideALastSeen?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sideBLastSeen", row.SideBLastSeen?.ToString("O") ?? (object)DBNull.Value);
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
                SideAId = reader.GetString(reader.GetOrdinal("side_a_id")),
                SideBId = reader.IsDBNull(reader.GetOrdinal("side_b_id")) ? null : reader.GetString(reader.GetOrdinal("side_b_id")),
                SideAVersion = reader.IsDBNull(reader.GetOrdinal("side_a_version")) ? null : reader.GetString(reader.GetOrdinal("side_a_version")),
                SideBVersion = reader.IsDBNull(reader.GetOrdinal("side_b_version")) ? null : reader.GetString(reader.GetOrdinal("side_b_version")),
                SideAHash = reader.IsDBNull(reader.GetOrdinal("side_a_hash")) ? null : reader.GetString(reader.GetOrdinal("side_a_hash")),
                SideBHash = reader.IsDBNull(reader.GetOrdinal("side_b_hash")) ? null : reader.GetString(reader.GetOrdinal("side_b_hash")),
                SideADeleted = reader.GetInt32(reader.GetOrdinal("side_a_deleted")) != 0,
                SideBDeleted = reader.GetInt32(reader.GetOrdinal("side_b_deleted")) != 0,
                SideALastSeen = reader.IsDBNull(reader.GetOrdinal("side_a_last_seen")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("side_a_last_seen"))),
                SideBLastSeen = reader.IsDBNull(reader.GetOrdinal("side_b_last_seen")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("side_b_last_seen"))),
                OriginSide = reader.IsDBNull(reader.GetOrdinal("origin_side")) ? null : reader.GetString(reader.GetOrdinal("origin_side")),
                LastSyncedAt = reader.IsDBNull(reader.GetOrdinal("last_synced_at")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_synced_at"))),
                LastSyncResult = reader.IsDBNull(reader.GetOrdinal("last_sync_result")) ? null : reader.GetString(reader.GetOrdinal("last_sync_result")),
                ConflictState = reader.IsDBNull(reader.GetOrdinal("conflict_state")) ? null : reader.GetString(reader.GetOrdinal("conflict_state")),
            });
        }

        return rows;
    }
}
