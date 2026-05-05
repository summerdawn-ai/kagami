using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;
using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// High-level service for interactive operations: list, export, sync, and import.
/// </summary>
public sealed class SyncService<TItem>(
    Func<string, IConnector<TItem>> connectorResolver,
    JobExecutor jobExecutor,
    StateDatabase stateDb,
    ILogger<SyncService<TItem>> logger) where TItem : CanonicalItem
{
    [SuppressMessage("ReSharper", "StaticMemberInGenericType", Justification = "It's generic")]
    private static readonly string[] ExportPhotoExtensions = [".png", ".jpg", ".gif", ".bmp", ".webp", ".bin"];

    /// <summary>
    /// Fetches all contacts from the named endpoint and returns them in order.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c> (e.g. <c>Microsoft</c>).</param>
    /// <param name="filter">Optional OData-style filter to apply in memory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<TItem>> ListAsync(
        string endpointName,
        IFilter<TItem>? filter = null,
        CancellationToken cancellationToken = default) =>
        await ListAsync(endpointName, filter, null, cancellationToken);

    /// <summary>
    /// Fetches contacts from the named endpoint and returns them in order.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c> (e.g. <c>Microsoft</c>).</param>
    /// <param name="filter">Optional OData-style filter to apply in memory.</param>
    /// <param name="maxItems">Optional maximum number of matching items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<TItem>> ListAsync(
        string endpointName,
        IFilter<TItem>? filter,
        int? maxItems,
        CancellationToken cancellationToken = default)
    {
        if (maxItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Maximum item count must be zero or greater.");
        }

        var connector = BuildConnector(endpointName);
        await connector.AuthenticateAsync(cancellationToken);

        List<TItem> items = [];
        var page = await connector.GetInitialPageAsync(cancellationToken);

        while (true)
        {
            foreach (var item in page.Items)
            {
                if (filter is not null && !filter.Matches(item))
                {
                    continue;
                }

                if (item is CanonicalContact contact)
                {
                    await ContactPhotoLoader.EnsureLoadedAsync(contact, cancellationToken);
                }

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
        IFilter<TItem>? filter = null,
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
        int successCount = 0;
        int failed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string baseName = BuildExportBaseName(item);
                nameCounts.TryGetValue(baseName, out int count);
                count++;
                nameCounts[baseName] = count;

                string fileName = count == 1 ? $"{baseName}.json" : $"{baseName}_{count}.json";
                string filePath = Path.Combine(outputDirectory, fileName);

                // Serialize the contact payload (or the full item if no typed payload)
                string json = SerializeExportItem(item);
                await File.WriteAllTextAsync(filePath, json, cancellationToken);
                logger.LogInformation("Exported contact to {File}", filePath);

                // Export photo for contacts
                if (item is CanonicalContact contact)
                {
                    if (ContactPhotoMetadata.TryGetPhoto(contact, out byte[] photoBytes, out string contentType))
                    {
                        string extension = ContactPhotoMetadata.GetFileExtension(contentType, photoBytes);
                        string photoPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(fileName) + extension);
                        await File.WriteAllBytesAsync(photoPath, photoBytes, cancellationToken);
                        logger.LogInformation("Exported contact photo to {File}", photoPath);
                    }
                }

                successCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Export: failed to export contact {ItemId}; skipping", item.Provenance.ProviderId);
            }
        }

        logger.LogInformation("Exported {Count} contact(s) to {Dir} ({Failed} failed)", successCount, outputDirectory, failed);
    }

    /// <summary>
    /// Synchronizes contacts between two configured endpoints.
    /// </summary>
    /// <param name="fromEndpoint">Name of the source endpoint.</param>
    /// <param name="toEndpoint">Name of the destination endpoint.</param>
    /// <param name="mode">Sync direction.</param>
    /// <param name="whatIf">When <c>true</c>, logs actions without writing any changes.</param>
    /// <param name="filter">Optional in-memory filter; only matching contacts are touched.</param>
    /// <param name="full">When <c>true</c>, re-enumerates both sides in full regardless of cursor state.</param>
    /// <param name="force">When <c>true</c>, unconditionally writes all in-scope items, bypassing sameness checks.</param>
    /// <param name="deletePolicy">Delete handling policy.</param>
    /// <param name="conflictPolicy">Conflict resolution policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JobExecutionResult> SyncAsync(
        string fromEndpoint,
        string toEndpoint,
        SyncMode mode = SyncMode.Forward,
        bool whatIf = false,
        IFilter<TItem>? filter = null,
        bool full = false,
        bool force = false,
        DeletePolicy deletePolicy = DeletePolicy.Ignore,
        ConflictPolicy conflictPolicy = ConflictPolicy.LastWriteWins,
        CancellationToken cancellationToken = default)
    {
        await stateDb.InitializeAsync(cancellationToken);

        var sourceConnector = BuildConnector(fromEndpoint);
        var destinationConnector = BuildConnector(toEndpoint);

        // Derive a stable job key from the two endpoint names so that sync state
        // is persisted consistently across invocations of the same contacts sync command.
        string jobKey = $"contacts:{fromEndpoint}:{toEndpoint}";

        var jobOptions = new JobOptions
        {
            Enabled = true,
            EntityType = ItemType.Contact,
            Source = fromEndpoint,
            Destination = toEndpoint,
            SyncMode = mode,
            DeletePolicy = deletePolicy,
            ConflictPolicy = conflictPolicy,
            Full = full,
            Force = force,
        };

        return await jobExecutor.ExecuteAsync(
            jobKey,
            jobOptions,
            sourceConnector,
            destinationConnector,
            whatIf,
            filter,
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
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports all possible types of TItem")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports all possible types of TItem")]
    public async Task<ImportResult> ImportAsync(
        string sourceDirectory,
        string toEndpoint,
        bool prune = false,
        IFilter<TItem>? filter = null,
        bool whatIf = false,
        CancellationToken cancellationToken = default)
    {
        var connector = BuildConnector(toEndpoint);
        await connector.AuthenticateAsync(cancellationToken);

        // Load all JSON files from the source directory
        string[] jsonFiles = Directory.GetFiles(sourceDirectory, "*.json");
        var importedItems = new List<TItem>();

        foreach (string jsonFile in jsonFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
            TItem? item;
            try
            {
                item = JsonSerializer.Deserialize<TItem>(json, KagamiJsonContext.ImportExportJsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Skipping {File}: failed to deserialize as {Type}: {Message}", jsonFile, typeof(TItem).Name, ex.Message);
                continue;
            }

            if (item is null)
            {
                logger.LogWarning("Skipping {File}: deserialized to null", jsonFile);
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(jsonFile);

            item.Provenance = new()
            {
                ProviderId = baseName
            };

            // Handle photo if item is contact
            if (item is CanonicalContact contact)
            {
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
                    ContactPhotoMetadata.SetPhoto(contact, photoBytes, contentType);
                    logger.LogDebug("Attached photo from {Photo} to contact {Name}", photoPath, baseName);
                }
                else
                {
                    // Explicitly clear photo (import is a true upsert including the photo property)
                    ContactPhotoMetadata.ClearPhoto(contact);
                }
            }

            if (filter is not null && !filter.Matches(item))
            {
                continue;
            }

            importedItems.Add(item);
        }

        // Load existing contacts from destination
        var page = await connector.GetInitialPageAsync(cancellationToken);
        var destinationItems = new List<TItem>(page.Items);
        while (page.HasMore && page.NextCursor is not null)
        {
            page = await connector.GetIncrementalPageAsync(page.NextCursor, cancellationToken);
            destinationItems.AddRange(page.Items);
        }

        var activeDestItems = destinationItems.Where(i => !i.IsDeleted).ToList();

        int created = 0;
        int updated = 0;
        int deleted = 0;

        // Track which destination items were matched/upserted
        var matchedDestIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var importItem in importedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Find matching destination contact
                var matches = activeDestItems
                    .Where(dest => !matchedDestIds.Contains(dest.Provenance.ProviderId) && ItemMatcher.IsImportMatch(importItem, dest))
                    .ToList();

                if (matches.Count == 0)
                {
                    // Create new contact
                    logger.LogInformation("Import: creating contact {Name}", importItem.Provenance.ProviderId);
                    if (!whatIf)
                    {
                        var createdItem = await connector.CreateItemAsync(importItem, cancellationToken);
                        matchedDestIds.Add(createdItem.Provenance.ProviderId);
                    }
                    created++;
                }
                else if (matches.Count == 1)
                {
                    // Update existing contact
                    var destContact = matches[0];
                    matchedDestIds.Add(destContact.Provenance.ProviderId);
                    logger.LogInformation("Import: updating contact {Name} (matched {DestId})", importItem.Provenance.ProviderId, destContact.Provenance.ProviderId);
                    if (!whatIf)
                    {
                        var targetItem = importItem with
                        {
                            Provenance = new()
                            {
                                // Keep destination provider id
                                ProviderId = destContact.Provenance.ProviderId
                            }
                        };

                        await connector.UpdateItemAsync(targetItem, cancellationToken);
                    }
                    updated++;
                }
                else
                {
                    // Multiple matches — ambiguous, skip
                    logger.LogWarning("Import: skipping contact {Name} — matched {Count} destination contacts (ambiguous)", importItem.Provenance.ProviderId, matches.Count);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import: failed to upsert contact {ItemId}; skipping", importItem.Provenance.ProviderId);
            }
        }

        // Prune: delete destination contacts not matched during import
        if (prune)
        {
            foreach (var destItem in activeDestItems)
            {
                if (!matchedDestIds.Contains(destItem.Provenance.ProviderId))
                {
                    try
                    {
                        logger.LogInformation("Import --prune: deleting contact {DestId}", destItem.Provenance.ProviderId);
                        if (!whatIf)
                        {
                            await connector.DeleteItemAsync(destItem.Provenance.ProviderId, cancellationToken);
                        }
                        deleted++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Import --prune: failed to delete contact {DestId}; skipping", destItem.Provenance.ProviderId);
                    }
                }
            }
        }

        logger.LogInformation("Import completed: {Created} created, {Updated} updated, {Deleted} deleted", created, updated, deleted);
        return new ImportResult { Created = created, Updated = updated, Deleted = deleted };
    }

    private IConnector<TItem> BuildConnector(string endpointName) => connectorResolver(endpointName);

    private static string BuildExportBaseName(CanonicalItem item) => item switch
    {
        CanonicalContact contact => ContactName.BuildExportBaseName(contact),
        _ => throw new ArgumentException("Item type not supported.")
    };

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "KagamiJsonContext supports all possible types of item")]
    [UnconditionalSuppressMessage("Trimming", "IL3050", Justification = "KagamiJsonContext supports all possible types of item")]
    private static string SerializeExportItem(CanonicalItem item) =>
        JsonSerializer.Serialize(item, KagamiJsonContext.ImportExportJsonOptions);
}
