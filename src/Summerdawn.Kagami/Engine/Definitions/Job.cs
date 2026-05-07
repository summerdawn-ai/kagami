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
    /// Gets the partition key used to group link-state rows for this endpoint pair.
    /// Derived from <see cref="JobOptions.EntityType"/>, <see cref="JobOptions.SourceEndpointName"/>, and
    /// <see cref="JobOptions.DestinationEndpointName"/> so that scheduled jobs and equivalent CLI sync runs
    /// targeting the same endpoints share the same link-state partition.
    /// </summary>
    public string PartitionKey => $"{Options.EntityType}:{Options.SourceEndpointName}:{Options.DestinationEndpointName}";
}
