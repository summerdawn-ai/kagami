namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Stores and retrieves endpoint sync cursors / tokens.
/// </summary>
public sealed class EndpointCursorRepository(StateDatabase db)
{
    /// <summary>
    /// Retrieves the cursor state for a named endpoint in a job, or <c>null</c> if none exists.
    /// </summary>
    public async Task<EndpointCursorState?> GetCursorAsync(string jobKey, string endpointName, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cursor_value, scope
            FROM job_endpoint_cursors
            WHERE job_key = @jobKey AND endpoint_name = @name
            """;
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@name", endpointName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EndpointCursorState(
            reader.GetString(reader.GetOrdinal("cursor_value")),
            reader.GetString(reader.GetOrdinal("scope")));
    }

    /// <summary>
    /// Upserts the cursor for a named endpoint in a job.
    /// </summary>
    public async Task SetCursorAsync(string jobKey, string endpointName, string scope, string cursor, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO job_endpoint_cursors (job_key, endpoint_name, scope, cursor_value, updated_at)
            VALUES (@jobKey, @name, @scope, @cursor, strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(job_key, endpoint_name) DO UPDATE SET
                scope        = excluded.scope,
                cursor_value = excluded.cursor_value,
                updated_at   = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@name", endpointName);
        cmd.Parameters.AddWithValue("@scope", scope);
        cmd.Parameters.AddWithValue("@cursor", cursor);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the cursor for a named endpoint in a job.
    /// </summary>
    public async Task DeleteCursorAsync(string jobKey, string endpointName, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM job_endpoint_cursors WHERE job_key = @jobKey AND endpoint_name = @name";
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@name", endpointName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Persisted cursor value and the filter scope it belongs to.
/// </summary>
/// <param name="Cursor">The stored incremental cursor.</param>
/// <param name="Scope">The raw filter scope string, or empty when unfiltered.</param>
public sealed record EndpointCursorState(string Cursor, string Scope);
