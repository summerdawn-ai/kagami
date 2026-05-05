using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Defines a sync job.
/// </summary>
/// <param name="Key">The job key used for lease and state tracking.</param>
/// <param name="Options">The job configuration.</param>
/// <param name="SourceConnector">Connector for source side.</param>
/// <param name="DestinationConnector">Connector for destination side.</param>
/// <param name="Filter">
/// Optional in-memory contact filter. Only items matching the filter are included in planning;
/// items not matching the filter are left completely untouched on both sides.
/// </param>
public record Job<TItem>(string Key, JobOptions Options, IConnector<TItem> SourceConnector, IConnector<TItem> DestinationConnector, IFilter<TItem>? Filter = null) where TItem : CanonicalItem;
