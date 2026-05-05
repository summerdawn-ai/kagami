using Microsoft.Extensions.Logging.Abstractions;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Derives sync actions from current job configuration, observed remote state, and persisted link state.
/// </summary>
/// <remarks>
/// This class is a compatibility shim that delegates to <see cref="SyncActionPlanner"/>.
/// New code should prefer constructing <see cref="SyncActionPlanner"/> directly.
/// </remarks>
public sealed class Planner(ILogger<Planner>? logger = null)
{
    private readonly SyncActionPlanner inner = new(new LinkCreator(NullLogger<LinkCreator>.Instance));

    /// <summary>
    /// Computes the full set of sync actions from current snapshots of both sides and existing links.
    /// </summary>
    public IReadOnlyList<SyncAction<TItem>> PlanActions<TItem>(
        JobOptions jobOptions,
        IReadOnlyList<TItem> sourceItems,
        IReadOnlyList<TItem> destinationItems,
        IReadOnlyList<LinkStateRow> existingLinks) where TItem : CanonicalItem =>
        inner.PlanActions(jobOptions, sourceItems, destinationItems, existingLinks);
}
