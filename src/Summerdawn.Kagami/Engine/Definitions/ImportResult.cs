namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents the outcome of a contacts import operation.
/// </summary>
public sealed class ImportResult
{
    /// <summary>
    /// Gets the number of contacts created.
    /// </summary>
    public int Created { get; init; }

    /// <summary>
    /// Gets the number of contacts updated.
    /// </summary>
    public int Updated { get; init; }

    /// <summary>
    /// Gets the number of contacts deleted (only populated when <c>--prune</c> is used).
    /// </summary>
    public int Deleted { get; init; }

    /// <summary>
    /// Gets the total number of contacts created or updated.
    /// </summary>
    public int TotalUpserted => Created + Updated;
}
