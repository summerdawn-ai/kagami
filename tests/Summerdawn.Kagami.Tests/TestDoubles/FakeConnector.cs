using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests.TestDoubles;

public sealed class FakeConnector : IConnector
{
    private readonly List<CanonicalItem> items = [];
    private int generation;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "fake",
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = false,
        SupportsContactPhotos = true,
        SupportsServerSideFiltering = false,
    };

    public void Seed(CanonicalItem item)
    {
        item.Metadata["gen"] = generation.ToString();
        items.Add(item);
    }

    public IReadOnlyList<CanonicalItem> Items => items;

    public void MarkDeleted(string id)
    {
        var item = items.FirstOrDefault(i => i.SourceId == id);
        if (item is not null)
        {
            generation++;
            item.IsDeleted = true;
            item.Metadata["gen"] = generation.ToString();
        }
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IncrementalPage> GetInitialPageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IncrementalPage
        {
            Items = [.. items],
            NextCursor = generation.ToString(),
            HasMore = false,
        });

    public Task<IncrementalPage> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(cursor, out int fromGeneration))
        {
            fromGeneration = 0;
        }

        var changed = items.Where(i => GetItemGeneration(i) > fromGeneration).ToList();
        return Task.FromResult(new IncrementalPage
        {
            Items = changed,
            NextCursor = generation.ToString(),
            HasMore = false,
        });
    }

    public Task<CanonicalItem?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(items.FirstOrDefault(i => i.SourceId == id));

    public Task<CanonicalItem> CreateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        generation++;
        var created = Clone(item);
        created.SourceId = Guid.NewGuid().ToString("N");
        created.Version = generation.ToString();
        created.Metadata["gen"] = generation.ToString();
        items.Add(created);
        return Task.FromResult(created);
    }

    public Task<CanonicalItem> UpdateItemAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        generation++;
        var existing = items.FirstOrDefault(i => i.SourceId == item.SourceId)
            ?? throw new InvalidOperationException($"Item {item.SourceId} not found in fake connector.");
        existing.Payload = item.Payload;
        existing.Version = generation.ToString();
        existing.ContentHash = item.ContentHash;
        existing.IsDeleted = item.IsDeleted;
        existing.Metadata = new Dictionary<string, string>(item.Metadata);
        existing.Metadata["gen"] = generation.ToString();
        return Task.FromResult(existing);
    }

    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        MarkDeleted(id);
        return Task.CompletedTask;
    }

    private static int GetItemGeneration(CanonicalItem item) =>
        item.Metadata.TryGetValue("gen", out string? genText) && int.TryParse(genText, out int gen) ? gen : 0;

    private static CanonicalItem Clone(CanonicalItem item) =>
        new()
        {
            EntityType = item.EntityType,
            Payload = item.Payload,
            SourceId = item.SourceId,
            Version = item.Version,
            ContentHash = item.ContentHash,
            IsDeleted = item.IsDeleted,
            Metadata = new Dictionary<string, string>(item.Metadata),
        };
}
