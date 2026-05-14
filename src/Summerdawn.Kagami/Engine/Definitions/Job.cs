using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents a sync job between a source and destination connector.
/// </summary>
public record Job<TItem>(string Key, JobOptions Options, IConnector<TItem> SourceConnector, IConnector<TItem> DestinationConnector) where TItem : CanonicalItem
{
    /// <summary>
    /// Gets the canonical job key used to partition link-state rows and cursors for this
    /// endpoint pair. Always in the format <c>contacts:{SourceEndpoint}:{DestinationEndpoint}</c>
    /// so that CLI sync runs targeting the same endpoints share consistent state regardless of
    /// how the <see cref="Key"/> was constructed.
    /// </summary>
    public string JobKey => $"contacts:{Options.SourceEndpointName}:{Options.DestinationEndpointName}";
}
