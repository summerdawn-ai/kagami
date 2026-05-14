namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Manages per-job leases to prevent overlapping execution.
/// </summary>
/// <remarks>
/// Each known job key always has exactly one row in <c>job_leases</c>. The <c>locked</c>
/// column is <c>1</c> while the job is running and <c>0</c> otherwise. Rows are never
/// deleted; use <see cref="ForceReleaseAsync"/> or <see cref="ForceReleaseAllAsync"/> to
/// clear stuck locks after a crash.
/// </remarks>
public sealed class LeaseRepository(StateDatabase db)
{
    /// <summary>
    /// Attempts to acquire a lock for the given job, creating the row if it does not yet exist.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the lock was acquired; <c>false</c> when the job is already locked.
    /// </returns>
    public async Task<bool> TryAcquireAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();

        // Upsert: insert a new row (locked=1) or update an existing unlocked row to locked=1.
        // The WHERE clause on DO UPDATE ensures we only take the lock when currently unlocked.
        cmd.CommandText = """
            INSERT INTO job_leases (job_key, locked) VALUES (@jobKey, 1)
            ON CONFLICT(job_key) DO UPDATE SET locked = 1
            WHERE job_leases.locked = 0
            """;

        cmd.Parameters.AddWithValue("@jobKey", jobKey);

        int affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    /// <summary>
    /// Releases the lock for the given job, keeping the row so the job remains visible in
    /// <see cref="ListAllAsync"/>.
    /// </summary>
    public async Task ReleaseAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE job_leases SET locked = 0 WHERE job_key = @jobKey";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Force-releases all job locks, keeping rows so jobs remain visible in
    /// <see cref="ListAllAsync"/>.
    /// </summary>
    /// <returns>The number of locked rows cleared.</returns>
    public async Task<int> ForceReleaseAllAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE job_leases SET locked = 0 WHERE locked = 1";
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Force-releases the lock for the specified job key.
    /// </summary>
    /// <returns>The number of rows updated (0 when the job is not known or already unlocked).</returns>
    public async Task<int> ForceReleaseAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE job_leases SET locked = 0 WHERE job_key = @jobKey AND locked = 1";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Determines whether the given job is currently locked.
    /// </summary>
    public async Task<bool> IsLockedAsync(string jobKey, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT locked FROM job_leases WHERE job_key = @jobKey";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long l && l != 0;
    }

    /// <summary>
    /// Returns all known jobs and their current lock state, ordered by job key.
    /// </summary>
    public async Task<IReadOnlyList<JobLeaseRow>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT job_key, locked FROM job_leases ORDER BY job_key";
        var rows = new List<JobLeaseRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new JobLeaseRow(
                reader.GetString(reader.GetOrdinal("job_key")),
                reader.GetInt32(reader.GetOrdinal("locked")) != 0));
        }

        return rows;
    }
}
