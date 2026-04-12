namespace Summerdawn.Kagami.Engine;

using System.Text;
using System.Text.Json;

using Summerdawn.Kagami.Configuration;
using Summerdawn.Kagami.Connectors;
using Summerdawn.Kagami.Models;
using Summerdawn.Kagami.Persistence;

/// <summary>
/// High-level service for interactive contacts operations: list, export, and sync.
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

    /// <summary>
    /// Fetches all contacts from the named endpoint and returns them in order.
    /// </summary>
    /// <param name="endpointName">The endpoint key in <c>appsettings.json</c> (e.g. <c>Microsoft</c>).</param>
    /// <param name="filter">Optional OData-style filter to apply in memory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CanonicalItem>> ListAsync(
        string endpointName,
        ContactFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var connector = BuildConnector(endpointName);
        await connector.AuthenticateAsync(cancellationToken);
        var page = await connector.GetInitialPageAsync(cancellationToken);
        IReadOnlyList<CanonicalItem> items = page.Items;
        if (filter is not null)
        {
            items = filter.Apply(items);
        }

        return items;
    }

    /// <summary>
    /// Exports contacts from the named endpoint to a local directory.
    /// One JSON file is written per contact, using the naming scheme
    /// <c>lastname_firstname[_N].json</c>. Any existing <c>*.json</c> files in
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
        }

        logger.LogInformation("Exported {Count} contact(s) to {Dir}", items.Count, outputDirectory);
    }

    /// <summary>
    /// Synchronizes contacts between two configured endpoints.
    /// </summary>
    /// <param name="fromEndpoint">Name of the source endpoint (side A).</param>
    /// <param name="toEndpoint">Name of the destination endpoint (side B).</param>
    /// <param name="mode">Sync direction.</param>
    /// <param name="whatIf">When <c>true</c>, logs actions without writing any changes.</param>
    /// <param name="filter">Optional in-memory filter; only matching contacts are touched.</param>
    /// <param name="force">
    /// When <c>true</c>, bypasses the HasChanged short-circuit so all in-scope contacts
    /// are re-evaluated, preserving last-write-wins conflict resolution.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JobExecutionResult> SyncAsync(
        string fromEndpoint,
        string toEndpoint,
        SyncMode mode = SyncMode.Bidirectional,
        bool whatIf = false,
        ContactFilter? filter = null,
        bool force = false,
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
            EndpointA = fromEndpoint,
            EndpointB = toEndpoint,
            SyncMode = mode,
            DeletePolicy = DeletePolicy.Mirror,
            ConflictPolicy = ConflictPolicy.LastWriteWins,
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
    {
        string lastName = string.Empty;
        string firstName = string.Empty;

        if (item.Payload is CanonicalContact contact)
        {
            lastName = contact.FamilyName ?? string.Empty;
            firstName = contact.GivenName ?? string.Empty;

            // Fall back to display name split if individual name parts are absent.
            // LastIndexOf treats the last whitespace-delimited word as the surname, which
            // is a reasonable heuristic for most Western display names (e.g., compound
            // first names or middle names will be grouped with GivenName).
            if (string.IsNullOrWhiteSpace(lastName) && string.IsNullOrWhiteSpace(firstName))
            {
                string display = contact.DisplayName ?? string.Empty;
                int spaceIdx = display.LastIndexOf(' ');
                if (spaceIdx > 0)
                {
                    // Everything before the last space → given name(s); last word → surname
                    firstName = display[..spaceIdx].Trim();
                    lastName = display[(spaceIdx + 1)..].Trim();
                }
                else
                {
                    lastName = display;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(lastName) && string.IsNullOrWhiteSpace(firstName))
        {
            // Last resort: use the provider ID
            return SanitizeSegment(item.SourceId);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(lastName))
        {
            sb.Append(SanitizeSegment(lastName));
        }

        if (!string.IsNullOrWhiteSpace(firstName))
        {
            if (sb.Length > 0)
            {
                sb.Append('_');
            }

            sb.Append(SanitizeSegment(firstName));
        }

        return sb.ToString();
    }

    private static string SanitizeSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c is ' ' or '-' or '.' or '\'' && sb.Length > 0)
            {
                // Replace word-separating punctuation with underscore
                sb.Append('_');
            }
        }

        // Collapse consecutive underscores
        string result = sb.ToString().Trim('_');
        while (result.Contains("__", StringComparison.Ordinal))
        {
            result = result.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }
}
