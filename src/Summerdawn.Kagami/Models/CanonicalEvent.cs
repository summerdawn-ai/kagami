namespace Summerdawn.Kagami.Models;

/// <summary>
/// Serves as the base representation for a provider-neutral calendar event.
/// </summary>
public record CanonicalEvent : CanonicalItem
{
    /// <summary>
    /// Gets or sets the event title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the event start timestamp.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the event end timestamp.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the event occupies whole calendar days.
    /// </summary>
    public bool IsAllDay { get; set; }

    /// <summary>
    /// Gets or sets the event description/body content.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the canonical iCalendar recurrence rule.
    /// </summary>
    public string? RecurrencePattern { get; set; }

    /// <summary>
    /// Gets or sets the event location.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets the event organizer.
    /// </summary>
    /// <remarks>
    /// The value is retained from provider reads and included in JSON exports, but connectors do
    /// not write it and it is excluded from the content hash used for content comparison.
    /// </remarks>
    public CalendarEventParticipant? Organizer { get; set; }

    /// <summary>
    /// Gets or sets the event attendees.
    /// </summary>
    /// <remarks>
    /// The values are retained from provider reads and included in JSON exports, but connectors do
    /// not write them and they are excluded from the content hash used for content comparison.
    /// </remarks>
    public List<CalendarEventParticipant> Attendees { get; set; } = [];

    /// <summary>
    /// Gets or sets the provider-supplied iCalendar UID.
    /// </summary>
    /// <remarks>
    /// The value is retained from provider reads and included in JSON exports, but it is not used
    /// for cross-provider matching, is not written by connectors, and is excluded from the content
    /// hash used for content comparison. Providers assign this value and do not allow it to be
    /// changed reliably.
    /// </remarks>
    public string? ICalUid { get; set; }

    /// <summary>
    /// Gets a value indicating whether the event includes attendees.
    /// </summary>
    /// <remarks>
    /// This derived value is included in JSON exports for convenience, but is not independently
    /// synchronized and is excluded from the content hash used for content comparison.
    /// </remarks>
    public bool HasAttendees => Attendees.Count > 0;

    /// <summary>
    /// Returns a human-readable display string for this event.
    /// </summary>
    public override string ToDisplayString() =>
        string.IsNullOrWhiteSpace(Title)
            ? base.ToDisplayString()
            : Title;
}

/// <summary>
/// Represents an event participant.
/// </summary>
public sealed record CalendarEventParticipant
{
    /// <summary>
    /// Gets or sets the participant display name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the participant email address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets the participant response status.
    /// </summary>
    public string? ResponseStatus { get; set; }
}
