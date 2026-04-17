namespace Summerdawn.Kagami.Configuration;

/// <summary>
/// Delete handling policy for a job.
/// </summary>
public enum DeletePolicy
{
    /// <summary>Mirror deletes to the other side.</summary>
    Mirror,
    /// <summary>Ignore deletes — do not propagate them.</summary>
    Ignore,
    /// <summary>Tombstone (soft-delete) the other side.</summary>
    Tombstone,
}
