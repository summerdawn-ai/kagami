namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Sync direction / mode policy for a job.
/// </summary>
public enum SyncMode
{
    /// <summary>Changes flow in both directions.</summary>
    Bidirectional,
    /// <summary>Changes flow from source to destination only.</summary>
    Forward,
    /// <summary>Changes flow from destination to source only. Kept for config-file compatibility; prefer swapping --from/--to instead.</summary>
    Reverse,
}
