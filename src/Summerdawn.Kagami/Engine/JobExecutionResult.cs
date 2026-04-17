namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Outcome of a <see cref="JobExecutor"/> run.
/// </summary>
public sealed class JobExecutionResult
{
    /// <summary>Job key.</summary>
    public string JobKey { get; set; } = string.Empty;

    /// <summary>Whether the job ran and succeeded.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Whether the job was skipped (e.g., due to a lease).</summary>
    public bool Skipped { get; set; }

    /// <summary>Reason the job was skipped.</summary>
    public string? SkipReason { get; set; }

    /// <summary>Total number of actions planned.</summary>
    public int ActionsPlanned { get; set; }

    /// <summary>Error details if the job failed.</summary>
    public string? Error { get; set; }
}
