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
    /// endpoint pair. Always in the format
    /// <c>{category}:{SourceEndpoint}:{DestinationEndpoint}</c>, where <c>{category}</c> is
    /// <c>contacts</c> or <c>events</c>, so that CLI sync runs targeting the same endpoints
    /// share consistent state regardless of how the <see cref="Key"/> was constructed.
    /// </summary>
    public string JobKey => CreateJobKey(Options.SourceEndpointName, Options.DestinationEndpointName);

    /// <summary>
    /// Gets the plural item category for this job.
    /// </summary>
    public string GetItemCategory() => GetItemCategoryCore();

    /// <summary>
    /// Creates the canonical sync job key for this item type and endpoint pair.
    /// </summary>
    public static string CreateJobKey(string sourceEndpointName, string destinationEndpointName) =>
        $"{GetItemCategoryCore()}:{sourceEndpointName}:{destinationEndpointName}";

    /// <summary>
    /// Creates an operation-specific job key for this item type.
    /// </summary>
    public static string CreateOperationKey(string operation, string left, string right) =>
        $"{GetItemCategoryCore()}:{operation}:{left}:{right}";

    private static string GetItemCategoryCore() =>
        typeof(TItem) == typeof(CanonicalContact)
            ? "contacts"
            : typeof(TItem) == typeof(CanonicalEvent)
                ? "events"
                : throw new NotSupportedException($"Jobs are not supported for item type '{typeof(TItem).FullName}'.");
}
