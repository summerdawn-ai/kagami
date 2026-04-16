namespace Summerdawn.Kagami.Engine;

using System.Text.Json;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

/// <summary>
/// High-level service for interactive contacts operations: list, export, sync, and import.
/// </summary>
public sealed class ContactsService(
    KagamiOptions options,
    IConnectorFactory connectorFactory,
    JobExecutor jobExecutor,
    StateDatabase stateDb,
    ILogger<ContactsService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly string[] ExportPhotoExtensions = [".png", ".jpg", ".gif", ".bmp", ".webp", ".bin"];

    /// <summary>
    /// Fetches all contacts from the named endpoint and returns them in order.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c> (e.g. <c>Microsoft</c>).</param>
    /// <param name="filter">Optional OData-style filter to apply in memory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CanonicalItem>> ListAsync(
        string endpointName,
        ContactFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        await ListAsync(endpointName, filter, null, cancellationToken);

    /// <summary>
    /// Fetches contacts from the named endpoint and returns them in order.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c> (e.g. <c>Microsoft</c>).</param>
    /// <param name="filter">Optional OData-style filter to apply in memory.</param>
    /// <param name="maxItems">Optional maximum number of matching items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CanonicalItem>> ListAsync(
        string endpointName,
        ContactFilter? filter,
        int? maxItems,
        CancellationToken cancellationToken = default)
    {
        if (maxItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Maximum item count must be zero or greater.");
        }

        var connector = BuildConnector(endpointName);
        await connector.AuthenticateAsync(cancellationToken);

        List<CanonicalItem> items = [];
        var page = await connector.GetInitialPageAsync(cancellationToken);

        while (true)
        {
            foreach (CanonicalItem item in page.Items)
            {
                if (filter is not null && !filter.Matches(item))
                {
                    continue;
                }

                await ContactPhotoLoader.EnsureLoadedAsync(item, cancellationToken);
                items.Add(item);
                if (maxItems is not null && items.Count >= maxItems.Value)
                {
                    return items;
                }
            }

            if (!page.HasMore)
            {
                return items;
            }

            if (page.NextCursor is null)
            {
                throw new InvalidOperationException($"Connector returned HasMore=true without a cursor for endpoint '{endpointName}'.");
            }

            page = await connector.GetIncrementalPageAsync(page.NextCursor, cancellationToken);
        }
    }

    /// <summary>
    /// Exports contacts from the named endpoint to a local directory.
    /// One JSON file is written per contact, using the effective contact name when available.
    /// Any existing <c>*.json</c> files and exported photo files in
    /// <paramref name="outputDirectory"/> are deleted before writing.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c>.</param>
    /// <param name="outputDirectory">The destination directory path.</param>
    /// <param name="filter">Optional OData-style filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExportAsync(
        string endpointName,
        string outputDirectory,
        ContactFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var items = await ListAsync(endpointName, filter, cancellationToken);
        Directory.CreateDirectory(outputDirectory);

        // Remove previously exported files
        foreach (string existing in Directory.GetFiles(outputDirectory, "*.json"))
        {
            File.Delete(existing);
            logger.LogDebug("Deleted existing export file {File}", existing);
        }

        foreach (string extension in ExportPhotoExtensions)
        {
            foreach (string existing in Directory.GetFiles(outputDirectory, $"*{extension}"))
            {
                File.Delete(existing);
                logger.LogDebug("Deleted existing export photo file {File}", existing);
            }
        }

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string baseName = BuildExportBaseName(item);
            nameCounts.TryGetValue(baseName, out int count);
            count++;
            nameCounts[baseName] = count;

            string fileName = count == 1 ? $"{baseName}.json" : $"{baseName}_{count}.json";
            string filePath = Path.Combine(outputDirectory, fileName);

            // Serialize the contact payload (or the full item if no typed payload)
            object payload = item.Payload ?? item;
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            logger.LogInformation("Exported contact to {File}", filePath);

            if (ContactPhotoMetadata.TryGetPhoto(item, out byte[] photoBytes, out string contentType))
            {
                string extension = ContactPhotoMetadata.GetFileExtension(contentType, photoBytes);
                string photoPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(fileName) + extension);
                await File.WriteAllBytesAsync(photoPath, photoBytes, cancellationToken);
                logger.LogInformation("Exported contact photo to {File}", photoPath);
            }
        }

        logger.LogInformation("Exported {Count} contact(s) to {Dir}", items.Count, outputDirectory);
    }

    /// <summary>
    /// Synchronizes contacts between two configured endpoints.
    /// </summary>
    /// <param name="fromEndpoint">Name of the source endpoint.</param>
    /// <param name="toEndpoint">Name of the destination endpoint.</param>
    /// <param name="mode">Sync direction.</param>
    /// <param name="whatIf">When <c>true</c>, logs actions without writing any changes.</param>
    /// <param name="filter">Optional in-memory filter; only matching contacts are touched.</param>
    /// <param name="force">
    /// When <c>true</c>, bypasses the HasChanged short-circuit so all in-scope contacts
    /// are re-evaluated, preserving last-write-wins conflict resolution.
    /// </param>
    /// <param name="deletePolicy">Delete handling policy.</param>
    /// <param name="conflictPolicy">Conflict resolution policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JobExecutionResult> SyncAsync(
        string fromEndpoint,
        string toEndpoint,
        SyncMode mode = SyncMode.Forward,
        bool whatIf = false,
        ContactFilter? filter = null,
        bool force = false,
        DeletePolicy deletePolicy = DeletePolicy.Ignore,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);

        var connectorA = BuildConnector(fromEndpoint);
        var connectorB = BuildConnector(toEndpoint);

        // Derive a stable job key from the two endpoint names so that sync state
        // is persisted consistently across invocations of the same contacts sync command.
        string jobKey = $"contacts:{fromEndpoint}:{toEndpoint}";

        var jobOptions = new JobOptions
        {
            Enabled = true,
            EntityType = EntityType.Contact,
            Source = fromEndpoint,
            Destination = toEndpoint,
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
        };

        return await jobExecutor.ExecuteAsync(
            jobKey,
            jobOptions,
            connectorA,
            connectorB,
            whatIf,
            filter,
            force,
            cancellationToken);
    }

    /// <summary>
    /// Imports contacts from a local directory of JSON files into the named endpoint.
    /// Each JSON file is deserialized as a <see cref="CanonicalContact"/>.
    /// If a same-name image file exists alongside the JSON, it is attached as the contact photo;
    /// if no image file is found, the photo is explicitly cleared.
    /// Contacts are upserted using the same matching logic as the sync engine.
    /// </summary>
    /// <param name="sourceDirectory">Path to the local directory containing JSON files.</param>
    /// <param name="toEndpoint">Name of the destination endpoint.</param>
    /// <param name="prune">
    /// When <c>true</c>, deletes any contact on the destination that did not appear in the import set.
    /// </param>
    /// <param name="filter">Optional OData-style filter applied to the JSON files before importing.</param>
    /// <param name="whatIf">When <c>true</c>, logs actions without writing any changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ImportResult> ImportAsync(
        string sourceDirectory,
        string toEndpoint,
        bool prune = false,
        ContactFilter? filter = null,
        bool whatIf = false,
        CancellationToken cancellationToken = default)
    {
        var connector = BuildConnector(toEndpoint);
        await connector.AuthenticateAsync(cancellationToken);

        // Load all JSON files from the source directory
        var jsonFiles = Directory.GetFiles(sourceDirectory, "*.json");
        var importedItems = new List<CanonicalItem>();

        foreach (string jsonFile in jsonFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
            CanonicalContact? contact;
            try
            {
                contact = JsonSerializer.Deserialize<CanonicalContact>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Skipping {File}: failed to deserialize as CanonicalContact: {Message}", jsonFile, ex.Message);
                continue;
            }

            if (contact is null)
            {
                logger.LogWarning("Skipping {File}: deserialized to null", jsonFile);
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(jsonFile);
            var item = new CanonicalItem
            {
                EntityType = EntityType.Contact,
                SourceId = baseName,
                Payload = contact,
            };

            // Look for a same-name image file
            string? photoPath = null;
            foreach (string ext in ExportPhotoExtensions)
            {
                string candidate = Path.Combine(sourceDirectory, baseName + ext);
                if (File.Exists(candidate))
                {
                    photoPath = candidate;
                    break;
                }
            }

            if (photoPath is not null)
            {
                byte[] photoBytes = await File.ReadAllBytesAsync(photoPath, cancellationToken);
                string contentType = ContactPhotoMetadata.GetContentTypeFromExtension(Path.GetExtension(photoPath));
                ContactPhotoMetadata.SetPhoto(item, photoBytes, contentType);
                logger.LogDebug("Attached photo from {Photo} to contact {Name}", photoPath, baseName);
            }
            else
            {
                // Explicitly clear photo (import is a true upsert including the photo property)
                ContactPhotoMetadata.ClearPhoto(item);
            }

            if (filter is not null && !filter.Matches(item))
            {
                continue;
            }

            importedItems.Add(item);
        }

        // Load existing contacts from destination
        var page = await connector.GetInitialPageAsync(cancellationToken);
        var destinationItems = new List<CanonicalItem>(page.Items);
        while (page.HasMore && page.NextCursor is not null)
        {
            page = await connector.GetIncrementalPageAsync(page.NextCursor, cancellationToken);
            destinationItems.AddRange(page.Items);
        }

        var activeDestItems = destinationItems.Where(i => !i.IsDeleted).ToList();
        var matchComparer = new ContactMatchComparer();

        int created = 0;
        int updated = 0;
        int deleted = 0;

        // Track which destination items were matched/upserted
        var matchedDestIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (CanonicalItem importItem in importedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find matching destination contact
            var matches = activeDestItems
                .Where(dest => !matchedDestIds.Contains(dest.SourceId) && IsImportMatch(importItem, dest, matchComparer))
                .ToList();

            if (matches.Count == 0)
            {
                // Create new contact
                logger.LogInformation("Import: creating contact {Name}", importItem.SourceId);
                if (!whatIf)
                {
                    var createdItem = await connector.CreateItemAsync(importItem, cancellationToken);
                    matchedDestIds.Add(createdItem.SourceId);
                }
                created++;
            }
            else if (matches.Count == 1)
            {
                // Update existing contact
                CanonicalItem destItem = matches[0];
                matchedDestIds.Add(destItem.SourceId);
                logger.LogInformation("Import: updating contact {Name} (matched {DestId})", importItem.SourceId, destItem.SourceId);
                if (!whatIf)
                {
                    var targetItem = new CanonicalItem
                    {
                        EntityType = importItem.EntityType,
                        SourceId = destItem.SourceId,
                        Payload = importItem.Payload,
                        Metadata = new Dictionary<string, string>(importItem.Metadata),
                    };
                    await connector.UpdateItemAsync(targetItem, cancellationToken);
                }
                updated++;
            }
            else
            {
                // Multiple matches — ambiguous, skip
                logger.LogWarning("Import: skipping contact {Name} — matched {Count} destination contacts (ambiguous)", importItem.SourceId, matches.Count);
            }
        }

        // Prune: delete destination contacts not matched during import
        if (prune)
        {
            foreach (CanonicalItem destItem in activeDestItems)
            {
                if (!matchedDestIds.Contains(destItem.SourceId))
                {
                    logger.LogInformation("Import --prune: deleting contact {DestId}", destItem.SourceId);
                    if (!whatIf)
                    {
                        await connector.DeleteItemAsync(destItem.SourceId, cancellationToken);
                    }
                    deleted++;
                }
            }
        }

        logger.LogInformation("Import completed: {Created} created, {Updated} updated, {Deleted} deleted", created, updated, deleted);
        return new ImportResult { Created = created, Updated = updated, Deleted = deleted };
    }

    /// <summary>
    /// Matches an import item against a destination item using ContactMatchComparer,
    /// with a fallback to display-name-only matching when neither side has contactable information.
    /// </summary>
    private static bool IsImportMatch(CanonicalItem importItem, CanonicalItem destItem, ContactMatchComparer comparer)
    {
        if (comparer.IsMatch(importItem, destItem))
        {
            return true;
        }

        if (importItem.Payload is CanonicalContact ic && destItem.Payload is CanonicalContact dc)
        {
            if (string.IsNullOrWhiteSpace(ic.DisplayName) || string.IsNullOrWhiteSpace(dc.DisplayName))
            {
                return false;
            }

            bool nameMatch = string.Equals(
                ic.DisplayName.Trim(),
                dc.DisplayName.Trim(),
                StringComparison.OrdinalIgnoreCase);

            bool importHasContactInfo = ic.Emails.Count > 0 || ic.Phones.Count > 0;
            bool destHasContactInfo = dc.Emails.Count > 0 || dc.Phones.Count > 0;
            return nameMatch && !importHasContactInfo && !destHasContactInfo;
        }

        return false;
    }

    private IConnector BuildConnector(string endpointName)
    {
        if (!options.Endpoints.TryGetValue(endpointName, out var endpoint))
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpointName}' not found in configuration. " +
                $"Available endpoints: {string.Join(", ", options.Endpoints.Keys)}");
        }

        options.Credentials.TryGetValue(endpoint.Credential, out var credential);
        return connectorFactory.Create(endpointName, endpoint, credential);
    }

    private static string BuildExportBaseName(CanonicalItem item)
        => ContactName.BuildExportBaseName(item);
}
