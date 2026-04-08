namespace Summerdawn.Kagami.Connectors.Fake;

using Summerdawn.Kagami.Models;

/// <summary>
/// In-memory deterministic connector for testing and development.
/// Simulates incremental sync by tracking a generation counter as the cursor.
/// </summary>
public sealed class FakeConnector : IConnector
{
    private readonly List<CanonicalItem> items = [];
    private int generation;

    /// <inheritdoc/>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "fake",
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = false,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = false,
    };

    /// <summary>
    /// Adds an item to the fake connector's store.
    /// </summary>
    public void Seed(CanonicalItem item) => items.Add(item);

    /// <summary>
    /// Marks an item as deleted in the fake store.
    /// </summary>
    public void MarkDeleted(string id)
    {
        var item = items.FirstOrDefault(i => i.SourceId == id);
        if (item is not null)
        {
            item.IsDeleted = true;
        }
    }

    /// <inheritdoc/>
    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default)
    {
        var cursor = generation.ToString();
        return Task.FromResult(new IncrementalPage
        {
            Items = [.. items],
            NextCursor = cursor,
            HasMore = false,
        });
    }

    /// <inheritdoc/>
    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default)
    {
        // Return items added/modified since the generation in cursor
        if (!int.TryParse(cursor, out int fromGen))
        {
            fromGen = 0;
        }

        var changed = items.Where(i => GetItemGeneration(i) > fromGen).ToList();
        return Task.FromResult(new IncrementalPage
        {
            Items = changed,
            NextCursor = generation.ToString(),
            HasMore = false,
        });
    }

    /// <inheritdoc/>
    public Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        var item = items.FirstOrDefault(i => i.SourceId == id);
        return Task.FromResult(item);
    }

    /// <inheritdoc/>
    public Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        generation++;
        var created = new CanonicalItem
        {
            EntityType = item.EntityType,
            Payload = item.Payload,
            SourceId = Guid.NewGuid().ToString("N"),
            Version = generation.ToString(),
            ContentHash = item.ContentHash,
            Metadata = new Dictionary<string, string>(item.Metadata) { ["gen"] = generation.ToString() },
        };
        items.Add(created);
        return Task.FromResult(created);
    }

    /// <inheritdoc/>
    public Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        generation++;
        var existing = items.FirstOrDefault(i => i.SourceId == item.SourceId);
        if (existing is null)
        {
            throw new InvalidOperationException($"Item {item.SourceId} not found in fake connector");
        }

        existing.Payload = item.Payload;
        existing.Version = generation.ToString();
        existing.Metadata["gen"] = generation.ToString();
        return Task.FromResult(existing);
    }

    /// <inheritdoc/>
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        generation++;
        MarkDeleted(id);
        return Task.CompletedTask;
    }

    private static int GetItemGeneration(CanonicalItem item)
    {
        if (item.Metadata.TryGetValue("gen", out var genStr) && int.TryParse(genStr, out int gen))
        {
            return gen;
        }

        return 0;
    }
}
