using System.Text.Json.Serialization;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Canonical model for a contact.
/// </summary>
public sealed class CanonicalContact : CanonicalItem
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string EntityType => Models.EntityType.Contact;

    /// <summary>Given (first) name.</summary>
    public string? GivenName { get; set; }

    /// <summary>Middle name.</summary>
    public string? MiddleName { get; set; }

    /// <summary>Family (last) name.</summary>
    public string? FamilyName { get; set; }

    /// <summary>Display name (computed or explicit).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Email addresses.</summary>
    public List<ContactEmail> Emails { get; set; } = [];

    /// <summary>Phone numbers.</summary>
    public List<ContactPhone> Phones { get; set; } = [];

    /// <summary>Physical addresses.</summary>
    public List<ContactAddress> Addresses { get; set; } = [];

    /// <summary>Organization / company name.</summary>
    public string? Organization { get; set; }

    /// <summary>Job title.</summary>
    public string? Title { get; set; }

    /// <summary>Notes / biography.</summary>
    public string? Notes { get; set; }

    /// <summary>Categories or labels attached to the contact.</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Birthday.</summary>
    public DateOnly? Birthday { get; set; }

    /// <inheritdoc/>
    public override CanonicalContact CloneWithProvenance(ItemProvenance provenance)
    {
        var clone = (CanonicalContact)MemberwiseClone();
        clone.Provenance = provenance;
        return clone;
    }
}

/// <summary>Email address entry.</summary>
public sealed class ContactEmail
{
    /// <summary>Address type label (e.g., "work", "home", "other").</summary>
    public string? Label { get; set; }
    /// <summary>Email address.</summary>
    public string Address { get; set; } = string.Empty;
}

/// <summary>Phone number entry.</summary>
public sealed class ContactPhone
{
    /// <summary>
    /// Phone type label. Standard labels: <c>home</c>, <c>work</c>, <c>mobile</c>,
    /// <c>other</c>, <c>pager</c>, <c>radio</c>, <c>assistant</c>, <c>main</c>.
    /// </summary>
    public string? Label { get; set; }
    /// <summary>Phone number.</summary>
    public string Number { get; set; } = string.Empty;
}

/// <summary>Physical address entry.</summary>
public sealed class ContactAddress
{
    /// <summary>Address type label (e.g., "work", "home").</summary>
    public string? Label { get; set; }
    /// <summary>Street line 1.</summary>
    public string? Street { get; set; }
    /// <summary>City.</summary>
    public string? City { get; set; }
    /// <summary>State or province.</summary>
    public string? State { get; set; }
    /// <summary>Postal code.</summary>
    public string? PostalCode { get; set; }
    /// <summary>Country.</summary>
    public string? Country { get; set; }
}
