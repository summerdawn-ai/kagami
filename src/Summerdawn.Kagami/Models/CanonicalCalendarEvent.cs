using System.Text.Json.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Canonical model for a calendar event.
/// </summary>
public sealed class CanonicalCalendarEvent : CanonicalItem
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string EntityType => Models.EntityType.CalendarEvent;

    /// <summary>Event subject/title.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Event body/description.</summary>
    public string? Body { get; set; }

    /// <summary>Start time (UTC).</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>End time (UTC).</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>Physical or virtual location.</summary>
    public string? Location { get; set; }

    /// <summary>Whether this is an all-day event.</summary>
    public bool IsAllDay { get; set; }

    /// <summary>Organizer email address.</summary>
    public string? OrganizerEmail { get; set; }

    /// <summary>Attendee email addresses.</summary>
    public List<string> AttendeeEmails { get; set; } = [];

    /// <summary>Whether the event has attendees (invited meeting).</summary>
    public bool HasAttendees => AttendeeEmails.Count > 0;

    /// <summary>iCalendar UID for cross-system correlation.</summary>
    public string? ICalUid { get; set; }

    /// <summary>Series master ID for recurring events.</summary>
    public string? SeriesMasterId { get; set; }

    /// <summary>Whether this is a recurring event.</summary>
    public bool IsRecurring { get; set; }

    /// <summary>Show-as / free-busy status.</summary>
    public string? ShowAs { get; set; }

    /// <summary>Importance/priority level.</summary>
    public string? Importance { get; set; }

    /// <inheritdoc/>
    public override CanonicalCalendarEvent CloneWithProvenance(ItemProvenance provenance)
    {
        var clone = (CanonicalCalendarEvent)MemberwiseClone();
        clone.Provenance = provenance;
        return clone;
    }
}
