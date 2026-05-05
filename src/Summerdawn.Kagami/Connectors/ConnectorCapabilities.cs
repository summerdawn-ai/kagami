namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Represents the capabilities declared by a connector.
/// </summary>
public sealed class ConnectorCapabilities
{
    /// <summary>
    /// Gets or sets the connector type identifier.
    /// </summary>
    public string ConnectorType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the connector supports incremental/delta sync via a cursor.
    /// </summary>
    public bool SupportsIncrementalSync { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connector can track and report deletes.
    /// </summary>
    public bool SupportsDeletes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connector supports attendee/meeting data.
    /// </summary>
    public bool SupportsAttendees { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connector supports recurring event series.
    /// </summary>
    public bool SupportsRecurrence { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connector supports reading and writing contact photos.
    /// </summary>
    public bool SupportsContactPhotos { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the connector supports server-side filtering.
    /// </summary>
    public bool SupportsServerSideFiltering { get; set; }
}
