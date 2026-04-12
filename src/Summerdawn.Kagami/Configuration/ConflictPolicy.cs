namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Conflict resolution policy for a job.
/// </summary>
public enum ConflictPolicy
{
    /// <summary>Most recently modified item wins.</summary>
    LastWriteWins,
    /// <summary>Side A always wins.</summary>
    SideAWins,
    /// <summary>Side B always wins.</summary>
    SideBWins,
    /// <summary>Flag the conflict and skip the update.</summary>
    Skip,
}
