
namespace Summerdawn.Kagami.Connectors.Microsoft;

/// <summary>
/// Maps canonical phone-number labels to Microsoft Graph contact phone fields and vice versa.
/// </summary>
/// <remarks>
/// Microsoft Graph v1.0 contacts expose three phone fields:
///   <c>businessPhones</c> (string array),
///   <c>homePhones</c>     (string array),
///   <c>mobilePhone</c>    (single string).
///
/// Every canonical label that has no exact counterpart is mapped to the closest
/// reasonable Microsoft field so that the phone number value is never dropped.
/// Round-tripping a label through Microsoft may change the label (e.g.
/// <c>other</c> → <c>homePhones</c> → <c>home</c>), but the number is preserved.
/// </remarks>
internal static class PhoneNumberMapper
{
    /// <summary>Microsoft Graph field name for business/work phones (string array).</summary>
    internal const string BusinessPhones = "businessPhones";

    /// <summary>Microsoft Graph field name for home phones (string array).</summary>
    internal const string HomePhones = "homePhones";

    /// <summary>Microsoft Graph field name for the mobile phone (single string).</summary>
    internal const string MobilePhone = "mobilePhone";

    /// <summary>
    /// Maps a canonical phone label to the Microsoft Graph contact field that should
    /// store numbers with that label.
    /// </summary>
    internal static string ToMicrosoftField(string? label) =>
        label?.ToLowerInvariant() switch
        {
            "work" => BusinessPhones,
            "home" => HomePhones,
            "mobile" => MobilePhone,
            // "other" is treated as a personal/home number – the closest Microsoft field.
            "other" => HomePhones,
            // Everything else (main, workFax, homeFax, pager, workMobile, etc.) falls back
            // to businessPhones to ensure the number is not lost.
            _ => BusinessPhones,
        };

    /// <summary>
    /// Maps a Microsoft Graph contact phone field name to the canonical label used
    /// when importing contacts from Microsoft.
    /// </summary>
    internal static string FromMicrosoftField(string microsoftField) =>
        microsoftField switch
        {
            BusinessPhones => "work",
            HomePhones => "home",
            MobilePhone => "mobile",
            _ => "other",
        };
}
