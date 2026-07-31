namespace Summerdawn.Kagami.Persistence;

/// <summary>
/// Represents a row from the <c>job_leases</c> table.
/// </summary>
/// <param name="JobKey">The canonical job key (e.g. <c>contacts:Microsoft:Google</c> or <c>events:Work:Archive</c>).</param>
/// <param name="Locked">Indicates whether the job is currently locked (i.e. running).</param>
public sealed record JobLeaseRow(string JobKey, bool Locked);
