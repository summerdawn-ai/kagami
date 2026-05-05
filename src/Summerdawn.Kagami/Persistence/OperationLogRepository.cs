namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Appends operation log entries for audit and debugging purposes.
/// </summary>
public sealed class OperationLogRepository(StateDatabase db)
{
    /// <summary>
    /// Appends a single operation log entry.
    /// </summary>
    public async Task AppendAsync(string jobKey, string entityType, string operation, string itemId, string side, string result, string? detail = null, CancellationToken cancellationToken = default)
    {
        await using var conn = db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operation_log (job_key, entity_type, operation, item_id, side, result, detail)
            VALUES (@jobKey, @entityType, @operation, @itemId, @side, @result, @detail)
            """;
        cmd.Parameters.AddWithValue("@jobKey", jobKey);
        cmd.Parameters.AddWithValue("@entityType", entityType);
        cmd.Parameters.AddWithValue("@operation", operation);
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@side", side);
        cmd.Parameters.AddWithValue("@result", result);
        cmd.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
