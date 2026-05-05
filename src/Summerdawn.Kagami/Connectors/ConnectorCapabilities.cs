namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Declares what a connector supports.
/// </summary>
public sealed class ConnectorCapabilities
{
    /// <summary>
    /// Connector type identifier.
    /// </summary>
    public string ConnectorType { get; set; } = string.Empty;

    /// <summary>
    /// Whether the connector supports incremental/delta sync via a cursor.
    /// </summary>
    public bool SupportsIncrementalSync { get; set; }

    /// <summary>
    /// Whether the connector can track and report deletes.
    /// </summary>
    public bool SupportsDeletes { get; set; }

    /// <summary>
    /// Whether the connector supports attendee/meeting data.
    /// </summary>
    public bool SupportsAttendees { get; set; }

    /// <summary>
    /// Whether the connector supports recurring event series.
    /// </summary>
    public bool SupportsRecurrence { get; set; }

    /// <summary>
    /// Whether the connector supports reading and writing contact photos.
    /// </summary>
    public bool SupportsContactPhotos { get; set; }

    /// <summary>
    /// Whether the connector supports server-side filtering.
    /// </summary>
    public bool SupportsServerSideFiltering { get; set; }
}
