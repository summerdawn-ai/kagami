namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Manages per-job leases to prevent overlapping execution.
/// </summary>
public sealed class LeaseRepository(StateDatabase db)
{
    /// <summary>Attempts to acquire a lease for the given job. Returns true if acquired.</summary>
    public async Task<bool> TryAcquireAsync(string jobKey, string holderId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(leaseDuration);

        // Insert only if no unexpired lease exists
        cmd.CommandText = """
            INSERT INTO job_leases (job_key, holder_id, acquired_at, expires_at)
            SELECT @jobKey, @holderId, @acquiredAt, @expiresAt
            WHERE NOT EXISTS (
                SELECT 1 FROM job_leases
                WHERE job_key = @jobKey
                  AND expires_at > @nowStr
            )
            ON CONFLICT(job_key) DO UPDATE SET
                holder_id   = excluded.holder_id,
                acquired_at = excluded.acquired_at,
                expires_at  = excluded.expires_at
            WHERE job_leases.expires_at <= @nowStr
            """;

        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@holderId", holderId);
        cmd.Parameters.AddWithValue("@acquiredAt", now.ToString("O"));
        cmd.Parameters.AddWithValue("@expiresAt", expires.ToString("O"));
        cmd.Parameters.AddWithValue("@nowStr", now.ToString("O"));

        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <summary>Releases the lease for the given job and holder.</summary>
    public async Task ReleaseAsync(string jobKey, string holderId, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM job_leases WHERE job_key = @jobKey AND holder_id = @holderId";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@holderId", holderId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Force-releases all job leases regardless of holder or expiry. Returns the number of locks cleared.</summary>
    public async Task<int> ForceReleaseAllAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM job_leases";
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Checks whether a valid (unexpired) lease exists for a job.</summary>
    public async Task<bool> IsLockedAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM job_leases WHERE job_key = @jobKey AND expires_at > @now";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        long count = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return count > 0;
    }
}
