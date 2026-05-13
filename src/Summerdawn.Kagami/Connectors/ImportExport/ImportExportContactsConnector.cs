using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// An <see cref="IConnector{TItem}"/> backed by a local directory of JSON files.
/// </summary>
/// <remarks>
/// <para>
/// Each contact is stored as a <c>{baseName}.json</c> file, where the base name is derived
/// from the contact's display name and used as the <see cref="ItemProvenance.ProviderId"/>.
/// Sibling image files with the same base name are attached as the contact photo on read and
/// written alongside the JSON on create or update.
/// </para>
/// <para>
/// Use as the source connector for import jobs (reads JSON files, presents them to the
/// planner as source items) and as the destination connector for export jobs (writes source
/// items to JSON files).
/// </para>
/// <para>
/// <see cref="DeleteItemAsync"/> always deletes the files when called.  The caller is
/// responsible for controlling whether delete actions are generated (via
/// <see cref="Configuration.DeletePolicy"/> on the job options).
/// </para>
/// </remarks>
public sealed class ImportExportContactsConnector(string directory) : IConnector<CanonicalContact>
{
    private static readonly string[] PhotoExtensions = [".png", ".jpg", ".gif", ".bmp", ".webp", ".bin"];

    // Tracks base names used during this session (pre-populated from existing files) to avoid
    // clobbering on create.
    private readonly HashSet<string> usedBaseNames = GetUsedBaseNames(directory);

    /// <inheritdoc/>
    public ConnectorCapabilities Capabilities { get; } = new()
    {
        ConnectorType = "json-contacts",
        SupportsIncrementalSync = false,
        SupportsDeletes = true,
        SupportsAttendees = false,
        SupportsRecurrence = false,
        SupportsContactPhotos = true,
        SupportsServerSideFiltering = false,
    };

    public string EndpointName => "importExport";

    /// <summary>
    /// No-op; local file access requires no authentication.
    /// </summary>
    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Reads all <c>*.json</c> files in the directory and returns them as a single item set.
    /// </summary>
    public async Task<ItemSet<CanonicalContact>> GetCursorItemsAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        if (cursor is not null)
        {
            throw new NotSupportedException("Import/Export does not support incremental cursors.");
        }

        var contacts = new List<CanonicalContact>();

        if (!Directory.Exists(directory))
        {
            return new ItemSet<CanonicalContact>(contacts, null);
        }

        foreach (string jsonFile in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string baseName = Path.GetFileNameWithoutExtension(jsonFile);
            var contact = await ReadContactAsync(jsonFile, baseName, cancellationToken);
            if (contact is not null)
            {
                contacts.Add(contact);
            }
        }

        return new ItemSet<CanonicalContact>(contacts, null);
    }

    /// <summary>
    /// Reads a single contact by its file base name (provider ID).
    /// </summary>
    public async Task<CanonicalContact?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string jsonFile = Path.Combine(directory, $"{id}.json");
        return File.Exists(jsonFile)
            ? await ReadContactAsync(jsonFile, id, cancellationToken)
            : null;
    }

    /// <summary>
    /// Writes the contact to a new JSON file, deriving a unique base name from the display name.
    /// </summary>
    public async Task<CanonicalContact> CreateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string baseName = AllocateUniqueBaseName(ContactNameHelper.BuildExportBaseName(item));
        string filePath = Path.Combine(directory, $"{baseName}.json");

        await WriteContactAsync(item, filePath, cancellationToken);

        return item with
        {
            Provenance = new() { ProviderId = baseName, Version = "1" }
        };
    }

    /// <summary>
    /// Overwrites the existing JSON and photo files for the contact identified by
    /// <see cref="ItemProvenance.ProviderId"/>.
    /// </summary>
    public async Task<CanonicalContact> UpdateItemAsync(CanonicalContact item, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, $"{item.Provenance.ProviderId}.json");
        await WriteContactAsync(item, filePath, cancellationToken);
        return item;
    }

    /// <summary>
    /// Deletes the JSON file and any sibling photo files for the given provider ID.
    /// </summary>
    public Task DeleteItemAsync(string id, CancellationToken cancellationToken = default)
    {
        string jsonFile = Path.Combine(directory, $"{id}.json");
        if (File.Exists(jsonFile))
        {
            File.Delete(jsonFile);
        }

        foreach (string ext in PhotoExtensions)
        {
            string photoFile = Path.Combine(directory, $"{id}{ext}");
            if (File.Exists(photoFile))
            {
                File.Delete(photoFile);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Allocates a unique base name by appending a numeric suffix when the desired name is
    /// already in use.
    /// </summary>
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

    /// <summary>
    /// Deserializes a contact from <paramref name="jsonFile"/> and attaches any sibling photo.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports CanonicalContact")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports CanonicalContact")]
    private async Task<CanonicalContact?> ReadContactAsync(string jsonFile, string baseName, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
        CanonicalContact? contact;
        try
        {
            contact = JsonSerializer.Deserialize<CanonicalContact>(json, KagamiJsonContext.ImportExportJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (contact is null)
        {
            return null;
        }

        contact.Provenance = new() { ProviderId = baseName };

        // Attach sibling photo if present.
        foreach (string ext in PhotoExtensions)
        {
            string photoPath = Path.Combine(directory, baseName + ext);
            if (File.Exists(photoPath))
            {
                byte[] photoBytes = await File.ReadAllBytesAsync(photoPath, cancellationToken);
                string contentType = ContactPhotoMetadataHelper.GetContentTypeFromExtension(ext);
                ContactPhotoMetadataHelper.SetPhoto(contact, photoBytes, contentType);
                break;
            }
        }

        return contact;
    }

    /// <summary>
    /// Serializes a contact to <paramref name="filePath"/> and writes any photo as a sibling file.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports CanonicalContact")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports CanonicalContact")]
    private async Task WriteContactAsync(CanonicalContact item, string filePath, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(item, KagamiJsonContext.ImportExportJsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        if (ContactPhotoMetadataHelper.TryGetPhoto(item, out byte[] photoBytes, out string contentType))
        {
            string extension = ContactPhotoMetadataHelper.GetFileExtension(contentType, photoBytes);
            string photoPath = Path.ChangeExtension(filePath, extension);
            await File.WriteAllBytesAsync(photoPath, photoBytes, cancellationToken);
        }
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
