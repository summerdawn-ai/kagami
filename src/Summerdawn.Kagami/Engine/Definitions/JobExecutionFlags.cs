namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Specifies runtime-only execution behavior for a single job run.
/// </summary>
[Flags]
public enum JobExecutionFlags
{
    /// <summary>
    /// Specifies default execution behavior.
    /// </summary>
    None = 0,

    /// <summary>
    /// Specifies that actions are logged but not executed.
    /// </summary>
    WhatIf = 1 << 0,

    /// <summary>
    /// Specifies that each action requires interactive confirmation before execution.
    /// </summary>
    Confirm = 1 << 1,
}
