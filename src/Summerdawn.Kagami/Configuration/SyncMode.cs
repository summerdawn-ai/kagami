namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Sync direction / mode policy for a job.
/// </summary>
public enum SyncMode
{
    /// <summary>Changes flow in both directions.</summary>
    Bidirectional,
    /// <summary>Changes flow from A to B only.</summary>
    AToB,
    /// <summary>Changes flow from B to A only.</summary>
    BToA,
}
