namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Signals that a saved incremental cursor can no longer be used and the caller must restart from a full read.
/// </summary>
public sealed class ExpiredCursorException(string message) : Exception(message);
