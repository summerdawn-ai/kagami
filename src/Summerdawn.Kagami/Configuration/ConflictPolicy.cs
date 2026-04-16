namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Conflict resolution policy for a job.
/// </summary>
public enum ConflictPolicy
{
    /// <summary>Most recently modified item wins.</summary>
    LastWriteWins,
    /// <summary>Source side always wins.</summary>
    SourceWins,
    /// <summary>Destination side always wins.</summary>
    DestinationWins,
    /// <summary>Flag the conflict and skip the update.</summary>
    Skip,
}
