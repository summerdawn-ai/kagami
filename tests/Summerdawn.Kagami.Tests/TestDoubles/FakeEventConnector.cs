using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests.TestDoubles;

public sealed class FakeEventConnector : IConnector<CanonicalEvent>
{
    private readonly List<CanonicalEvent> items = [];
    private int generation;

    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "fake-events",
        SupportsIncrementalSync = true,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = false,
    };

    public string EndpointName => "fake";

    public IReadOnlyList<CanonicalEvent> Items => items;

    public void Seed(CanonicalEvent item)
    {
        item.Metadata["gen"] = generation.ToString();
        items.Add(item);
    }

    public void MarkDeleted(string id)
    {
        var item = items.FirstOrDefault(i => i.Provenance.ProviderId == id);
        if (item is not null)
        {
            generation++;
            item.IsDeleted = true;
            item.Metadata["gen"] = generation.ToString();
        }
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ItemSet<CanonicalEvent>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        if (cursor is null)
        {
            return Task.FromResult(new ItemSet<CanonicalEvent>([.. items], generation.ToString()));
        }

        if (!int.TryParse(cursor, out int fromGeneration))
        {
            fromGeneration = 0;
        }

        var changed = items.Where(i => GetItemGeneration(i) > fromGeneration).ToList();
        return Task.FromResult(new ItemSet<CanonicalEvent>(changed, generation.ToString()));
    }

    public Task<CanonicalEvent?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(items.FirstOrDefault(i => i.Provenance.ProviderId == id));

    public Task<CanonicalEvent> CreateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        generation++;

        var created = item with
        {
            Metadata = new(item.Metadata)
            {
                ["gen"] = generation.ToString()
            },

            Provenance = new()
            {
                ProviderId = Guid.NewGuid().ToString("N"),
                Version = generation.ToString(),
            }
        };

        items.Add(created);
        return Task.FromResult(created);
    }

    public Task<CanonicalEvent> UpdateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        generation++;
        var existing = items.FirstOrDefault(i => i.Provenance.ProviderId == item.Provenance.ProviderId)
            ?? throw new InvalidOperationException($"Item {item.Provenance.ProviderId} not found in fake connector.");

        var updated = item with
        {
            Metadata = new(item.Metadata)
            {
                ["gen"] = generation.ToString()
            },

            Provenance = new()
            {
                ProviderId = item.Provenance.ProviderId,
                Version = generation.ToString(),
            }
        };

        items.Remove(existing);
        items.Add(updated);

        return Task.FromResult(updated);
    }

    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        MarkDeleted(id);
        return Task.CompletedTask;
    }

    private static int GetItemGeneration(CanonicalItem item) =>
        item.Metadata.TryGetValue("gen", out string? genText) && int.TryParse(genText, out int gen) ? gen : 0;
}
