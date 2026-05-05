using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Represents a sync job between a source and destination connector.
/// </summary>

public record Job<TItem>(string Key, JobOptions Options, IConnector<TItem> SourceConnector, IConnector<TItem> DestinationConnector, IFilter<TItem>? Filter = null) where TItem : CanonicalItem;
