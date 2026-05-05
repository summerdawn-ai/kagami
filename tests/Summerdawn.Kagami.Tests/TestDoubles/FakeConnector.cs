using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests.TestDoubles;

public sealed class FakeConnector : IConnector<CanonicalContact>
{
    private readonly List<CanonicalContact> items = [];
    private int generation;
    private int getInitialPageCallCount;

    /// <summary>
    /// Gets the number of times <see cref="GetInitialPageAsync"/> has been called.
    /// </summary>
    public int GetInitialPageCallCount => getInitialPageCallCount;

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

    public void Seed(CanonicalContact item)
    {
        item.Metadata["gen"] = generation.ToString();
        items.Add(item);
    }

    public IReadOnlyList<CanonicalContact> Items => items;

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

    public Task<IncrementalPage<CanonicalContact>> GetInitialPageAsync(CancellationToken cancellationToken = default)
    {
        getInitialPageCallCount++;
        return Task.FromResult(new IncrementalPage<CanonicalContact>
        {
            Items = [.. items],
            NextCursor = generation.ToString(),
            HasMore = false,
        });
    }

    public Task<IncrementalPage<CanonicalContact>> GetIncrementalPageAsync(string cursor, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(cursor, out int fromGeneration))
        {
            fromGeneration = 0;
        }

        var changed = items.Where(i => GetItemGeneration(i) > fromGeneration).ToList();
        return Task.FromResult(new IncrementalPage<CanonicalContact>
        {
            Items = changed,
            NextCursor = generation.ToString(),
            HasMore = false,
        });
    }

    public Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(items.FirstOrDefault(i => i.Provenance.ProviderId == id));

    public Task<CanonicalContact> CreateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default)
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

    public Task<CanonicalContact> UpdateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default)
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
