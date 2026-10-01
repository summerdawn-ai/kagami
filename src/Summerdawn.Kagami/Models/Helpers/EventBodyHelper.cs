using System.Net;
using System.Text.RegularExpressions;

namespace Summerdawn.Kagami.Models;

/// <summary>
/// Converts event descriptions between their canonical form and Microsoft Graph body payloads.
/// </summary>
/// <remarks>
/// Google stores a description as one string that may or may not contain HTML. Microsoft Graph
/// always returns an HTML document, even for a body that was written as plain text, and Exchange
/// adds a document wrapper and layout newlines around the actual content. This type chooses the
/// content type to write and removes that Exchange-generated markup when reading, so a description
/// round-trips between the providers without accumulating whitespace.
/// </remarks>
internal static partial class EventBodyHelper
{
    private const string HtmlContentType = "html";
    private const string TextContentType = "text";

    /// <summary>
    /// Chooses the Graph body content type for a canonical description.
    /// </summary>
    /// <remarks>
    /// Text that contains no HTML elements or entities is written as text, because HTML would
    /// collapse its line breaks. Anything that looks like HTML is written unchanged as HTML.
    /// </remarks>
    public static (string ContentType, string Content) ToMicrosoftBody(string? description)
    {
        description ??= string.Empty;
        return (HtmlMarkupRegex().IsMatch(description) ? HtmlContentType : TextContentType, description);
    }

    /// <summary>
    /// Extracts the canonical description from the HTML body returned by Microsoft Graph.
    /// </summary>
    /// <returns>
    /// The description, or <c>null</c> when the body is empty. Content that does not match a known
    /// Exchange shape is returned unchanged.
    /// </returns>
    public static string? FromMicrosoftBody(string? graphHtml)
    {
        if (string.IsNullOrWhiteSpace(graphHtml))
        {
            return null;
        }

        // Exchange renders a plain-text body as HTML with a fixed marker; decode it back to the original text.
        var converted = ConvertedTextRegex().Match(graphHtml);
        if (converted.Success)
        {
            string text = converted.Groups["text"].Value.Replace("<br>\r\n", "\n").Replace("<br>", "\n");
            return NullIfEmpty(WebUtility.HtmlDecode(text));
        }

        var body = BodyRegex().Match(graphHtml);
        if (!body.Success)
        {
            return graphHtml;
        }

        // Newlines inside <pre> are content; every other newline is layout added by Exchange.
        string content = string.Concat(PreformattedRegex().Split(body.Groups["body"].Value.Trim())
            .Select(static (segment, index) => index % 2 == 0 ? RemoveLayoutNewlines(segment) : segment));
        return NullIfEmpty(content);
    }

    /// <summary>
    /// Removes newlines that Exchange inserts next to tags and at its line-wrap points.
    /// </summary>
    /// <remarks>
    /// A newline next to a tag or a space is layout and is removed. A newline directly between two
    /// words stands in for whitespace and becomes a space.
    /// </remarks>
    private static string RemoveLayoutNewlines(string html) =>
        NewlineRegex().Replace(AdjacentNewlineRegex().Replace(html, string.Empty), " ");

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    [GeneratedRegex(@"<\/?(?:html|head|body|a|abbr|b|big|blockquote|br|center|code|dd|div|dl|dt|em|font|h[1-6]|hr|i|img|li|ol|p|pre|s|small|span|strike|strong|style|sub|sup|table|tbody|td|th|thead|tr|u|ul)(\s[^<>]*)?\/?>|&(#[0-9]+|#x[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*);", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlMarkupRegex();

    [GeneratedRegex(@"<!-- converted from text -->.*?<div class=""PlainText"">(?<text>.*?)</div></span></font>", RegexOptions.Singleline)]
    private static partial Regex ConvertedTextRegex();

    [GeneratedRegex(@"<body\b[^>]*>(?<body>.*)</body\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"(<pre\b.*?</pre\s*>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PreformattedRegex();

    [GeneratedRegex(@"(?<=[> ])\r?\n|\r?\n(?=[< ])")]
    private static partial Regex AdjacentNewlineRegex();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex NewlineRegex();
}
