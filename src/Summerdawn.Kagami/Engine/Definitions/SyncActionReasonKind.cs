namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Specifies the structured rationale for a planned synchronization action.
/// </summary>
public enum SyncActionReasonKind
{
    /// <summary>
    /// Indicates that the action uses its operation kind's default rationale.
    /// </summary>
    Default,

    /// <summary>
    /// Indicates that the originating item is newer than the item it will replace.
    /// </summary>
    Newer,

    /// <summary>
    /// Indicates that force mode writes an otherwise identical matched item.
    /// </summary>
    Forced,

    /// <summary>
    /// Indicates that a conflict policy selected a winning item.
    /// </summary>
    ConflictWinner,

    /// <summary>
    /// Indicates that the newer item cannot be written in the requested sync mode.
    /// </summary>
    ConflictNewerCannotUpdate,

    /// <summary>
    /// Indicates that a policy-selected item cannot be written in the requested sync mode.
    /// </summary>
    ConflictWinnerCannotUpdate,

    /// <summary>
    /// Indicates that a conflict policy skipped an item changed on both sides.
    /// </summary>
    ConflictSkipped,
}
