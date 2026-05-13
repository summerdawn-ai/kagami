namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents a sync operation that faulted and must not advance its cursor.
/// </summary>
/// <remarks>
/// Thrown in two cases:
/// <list type="bullet">
///   <item>A transient provider failure (such as a 429 Too Many Requests or any 5xx server
///   error) is encountered mid-pass: the entire run is aborted immediately so that no
///   further writes are attempted against an unreliable provider.</item>
///   <item>One or more per-item permanent failures occurred during the pass: processing
///   continued for the remaining items, but the run as a whole is marked faulted.</item>
/// </list>
/// In both cases the cursor is not advanced, so the same event window is retried on the
/// next run. Because link-state rows written before the fault are idempotent, items that
/// were already synced successfully will be recognised as unchanged and skipped cheaply on
/// replay.
/// </remarks>
public sealed class OperationFaultedException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="OperationFaultedException"/> with a message.
    /// </summary>
    public OperationFaultedException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of <see cref="OperationFaultedException"/> with a message
    /// and an inner exception.
    /// </summary>
    public OperationFaultedException(string message, Exception innerException) : base(message, innerException) { }
}
