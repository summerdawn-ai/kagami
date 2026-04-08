namespace Summerdawn.Kagami.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>
/// Stores and retrieves endpoint sync cursors / tokens.
/// </summary>
public sealed class EndpointCursorRepository(StateDatabase db)
{
    /// <summary>Retrieves the cursor for a named endpoint, or null if none exists.</summary>
    public async Task<string?> GetCursorAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT cursor_value FROM endpoint_cursors WHERE endpoint_name = @name";
        cmd.Parameters.AddWithValue("@name", endpointName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : (string)result;
    }

    /// <summary>Upserts the cursor for a named endpoint.</summary>
    public async Task SetCursorAsync(string endpointName, string cursor, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO endpoint_cursors (endpoint_name, cursor_value, updated_at)
            VALUES (@name, @cursor, strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            ON CONFLICT(endpoint_name) DO UPDATE SET
                cursor_value = excluded.cursor_value,
                updated_at   = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@name", endpointName);
        cmd.Parameters.AddWithValue("@cursor", cursor);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes the cursor for a named endpoint.</summary>
    public async Task DeleteCursorAsync(string endpointName, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM endpoint_cursors WHERE endpoint_name = @name";
        cmd.Parameters.AddWithValue("@name", endpointName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
