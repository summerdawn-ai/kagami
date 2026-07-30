using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// An <see cref="IConnector{TItem}"/> backed by a local directory of event JSON files.
/// </summary>
/// <remarks>
/// <para>
/// Each event is stored as a <c>{baseName}.json</c> file, where the base name is derived from
/// the event title and start time and used as the <see cref="ItemProvenance.ProviderId"/>.
/// </para>
/// <para>
/// Use as the source connector for import jobs and as the destination connector for export jobs.
/// </para>
/// </remarks>
public sealed class ImportExportEventsConnector(string directory) : IConnector<CanonicalEvent>
{
    private readonly HashSet<string> usedBaseNames = GetUsedBaseNames(directory);

    /// <inheritdoc/>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "json-events",
        SupportsIncrementalSync = false,
        SupportsDeletes = true,
        SupportsAttendees = true,
        SupportsRecurrence = true,
        SupportsContactPhotos = false,
        SupportsServerSideFiltering = false,
    };

    /// <inheritdoc/>
    public string EndpointName => "importExport";

    /// <inheritdoc/>
    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<ItemSet<CanonicalEvent>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        if (cursor is not null)
        {
            throw new NotSupportedException("Import/Export does not support incremental cursors.");
        }

        var events = new List<CanonicalEvent>();

        if (!Directory.Exists(directory))
        {
            return new ItemSet<CanonicalEvent>(events, null);
        }

        foreach (string jsonFile in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string baseName = Path.GetFileNameWithoutExtension(jsonFile);
            var calendarEvent = await ReadEventAsync(jsonFile, baseName, cancellationToken);
            if (calendarEvent is not null)
            {
                events.Add(calendarEvent);
            }
        }

        return new ItemSet<CanonicalEvent>(events, null);
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string jsonFile = Path.Combine(directory, $"{id}.json");
        return File.Exists(jsonFile)
            ? await ReadEventAsync(jsonFile, id, cancellationToken)
            : null;
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent> CreateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string baseName = AllocateUniqueBaseName(EventNameHelper.BuildExportBaseName(item));
        string filePath = Path.Combine(directory, $"{baseName}.json");

        await WriteEventAsync(item, filePath, cancellationToken);

        return item with
        {
            Provenance = new() { ProviderId = baseName, Version = "1" }
        };
    }

    /// <inheritdoc/>
    public async Task<CanonicalEvent> UpdateItemAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, $"{item.Provenance.ProviderId}.json");
        await WriteEventAsync(item, filePath, cancellationToken);
        return item;
    }

    /// <inheritdoc/>
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string jsonFile = Path.Combine(directory, $"{id}.json");
        if (File.Exists(jsonFile))
        {
            File.Delete(jsonFile);
        }

        return Task.CompletedTask;
    }

    private string AllocateUniqueBaseName(string desiredBaseName)
    {
        if (usedBaseNames.Add(desiredBaseName))
        {
            return desiredBaseName;
        }

        for (int count = 2; ; count++)
        {
            string candidate = $"{desiredBaseName}_{count}";
            if (usedBaseNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports CanonicalEvent")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports CanonicalEvent")]
    private static async Task<CanonicalEvent?> ReadEventAsync(string jsonFile, string baseName, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
        CanonicalEvent? calendarEvent;
        try
        {
            calendarEvent = JsonSerializer.Deserialize<CanonicalEvent>(json, KagamiJsonContext.ImportExportJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (calendarEvent is null)
        {
            return null;
        }

        calendarEvent.Provenance = new() { ProviderId = baseName };
        return calendarEvent;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports CanonicalEvent")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports CanonicalEvent")]
    private static async Task WriteEventAsync(CanonicalEvent item, string filePath, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(item, KagamiJsonContext.ImportExportJsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private static HashSet<string> GetUsedBaseNames(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return names;
    }
}
