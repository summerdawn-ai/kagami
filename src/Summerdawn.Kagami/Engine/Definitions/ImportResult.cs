namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Result of a contacts import operation.
/// </summary>
public sealed class ImportResult
{
    /// <summary>
    /// Number of contacts created.
    /// </summary>
    public int Created { get; init; }

    /// <summary>
    /// Number of contacts updated.
    /// </summary>
    public int Updated { get; init; }

    /// <summary>
    /// Number of contacts deleted (only populated when <c>--prune</c> is used).
    /// </summary>
    public int Deleted { get; init; }

    /// <summary>
    /// Total contacts processed (created + updated).
    /// </summary>
    public int TotalUpserted => Created + Updated;
}
