namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Declares what a connector supports.
/// </summary>
public sealed class ConnectorCapabilities
{
    /// <summary>Connector type identifier.</summary>
    public string ConnectorType { get; set; } = string.Empty;

    /// <summary>Whether the connector supports incremental/delta sync.</summary>
    public bool SupportsIncrementalSync { get; set; }

    /// <summary>Whether the connector can track deletes.</summary>
    public bool SupportsDeletes { get; set; }

    /// <summary>Whether the connector supports attendee/meeting data.</summary>
    public bool SupportsAttendees { get; set; }

    /// <summary>Whether the connector supports recurring events.</summary>
    public bool SupportsRecurrence { get; set; }

    /// <summary>Whether the connector supports contact photos.</summary>
    public bool SupportsContactPhotos { get; set; }

    /// <summary>Whether the connector supports server-side filtering.</summary>
    public bool SupportsServerSideFiltering { get; set; }
}
