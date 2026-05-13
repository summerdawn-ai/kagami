namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents the outcome of a <see cref="JobExecutor"/> run.
/// </summary>
public sealed class JobExecutionResult
{
    /// <summary>
    /// Gets or sets the job key.
    /// </summary>
    public string JobKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the job ran and completed successfully.
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the job was skipped (e.g., because a lease was already held).
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// Gets or sets a human-readable reason the job was skipped.
    /// </summary>
    public string? SkipReason { get; set; }

    /// <summary>
    /// Gets or sets the total number of write actions planned (excludes Skip/None actions).
    /// </summary>
    public int ActionsPlanned { get; set; }

    /// <summary>
    /// Gets or sets the error message when the job failed.
    /// </summary>
    public string? Error { get; set; }
}
