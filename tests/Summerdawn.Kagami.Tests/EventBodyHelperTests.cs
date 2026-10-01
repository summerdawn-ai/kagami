using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Tests;

public sealed class EventBodyHelperTests
{
    [Fact]
    public void FromMicrosoftBody_StripsExchangeDocumentWrapperFromPlainContent()
    {
        const string graphHtml = "<html>\r\n<head>\r\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n</head>\r\n<body>\r\nFoo\r\n</body>\r\n</html>\r\n";

        Assert.Equal("Foo", EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Fact]
    public void FromMicrosoftBody_RestoresRichHtmlWrittenAsAFragment()
    {
        const string graphHtml = "<html>\r\n<head>\r\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n</head>\r\n<body>\r\n<b>Foo</b><br>\r\n<br>\r\n<i>Bar</i> &amp; <a href=\"https://example.com\">link</a>\r\n</body>\r\n</html>\r\n";

        Assert.Equal(
            "<b>Foo</b><br><br><i>Bar</i> &amp; <a href=\"https://example.com\">link</a>",
            EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Fact]
    public void FromMicrosoftBody_RemovesExchangeLineWrapsButKeepsTheSpace()
    {
        const string graphHtml = "<html>\r\n<head>\r\n</head>\r\n<body>\r\nSunset is at\r\n 7pm, you might wanna be here\r\n</body>\r\n</html>\r\n";

        Assert.Equal("Sunset is at 7pm, you might wanna be here", EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Fact]
    public void FromMicrosoftBody_ReplacesNewlineBetweenWordsWithSpace()
    {
        Assert.Equal("Foo Bar", EventBodyHelper.FromMicrosoftBody("<html><body>Foo\r\nBar</body></html>"));
    }

    [Fact]
    public void FromMicrosoftBody_KeepsNewlinesInsidePreformattedBlocks()
    {
        const string graphHtml = "<html>\r\n<head>\r\n</head>\r\n<body>\r\n<pre>Foo\n\nBar</pre>\r\n</body>\r\n</html>\r\n";

        Assert.Equal("<pre>Foo\n\nBar</pre>", EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Fact]
    public void FromMicrosoftBody_RestoresTextWrittenAsTextExactly()
    {
        const string graphHtml = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n<meta name=\"Generator\" content=\"Microsoft Exchange Server\">\r\n<!-- converted from text -->\r\n<style><!-- .EmailQuote { margin-left: 1pt; padding-left: 4pt; border-left: #800000 2px solid; } --></style></head>\r\n<body>\r\n<font size=\"2\"><span style=\"font-size:11pt;\"><div class=\"PlainText\"><br>\r\nFoo &lt;literal&gt; &amp; bar<br>\r\n<br>\r\nBaz<br>\r\n</div></span></font>\r\n</body>\r\n</html>\r\n";

        Assert.Equal("\nFoo <literal> & bar\n\nBaz\n", EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Fact]
    public void FromMicrosoftBody_KeepsHtmlThatOutlookSavedAfterEditingTextBody()
    {
        const string graphHtml = "<html>\r\n<head>\r\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n</head>\r\n<body>\r\n<div class=\"PlainText\" style=\"font-size:11pt\">First line!<br>\r\n<br>\r\nThird line<br>\r\nFourth &lt;line&gt; &amp; more</div>\r\n</body>\r\n</html>\r\n";

        Assert.Equal(
            "<div class=\"PlainText\" style=\"font-size:11pt\">First line!<br><br>Third line<br>Fourth &lt;line&gt; &amp; more</div>",
            EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>\r\n<head>\r\n</head>\r\n<body>\r\n</body>\r\n</html>\r\n")]
    public void FromMicrosoftBody_ReturnsNullForEmptyBody(string? graphHtml)
    {
        Assert.Null(EventBodyHelper.FromMicrosoftBody(graphHtml));
    }

    [Fact]
    public void FromMicrosoftBody_ReturnsUnrecognizedContentUnchanged()
    {
        Assert.Equal("Agenda\r\nItem", EventBodyHelper.FromMicrosoftBody("Agenda\r\nItem"));
    }

    [Theory]
    [InlineData("Foo\n\nBar", "text")]
    [InlineData("", "text")]
    [InlineData("Meeting with <joe@example.com>", "text")]
    [InlineData("Meeting with <a@example.com>", "text")]
    [InlineData("Foo <literal> & bar\n\nBaz", "text")]
    [InlineData("1 < 2 and 3 > 2", "text")]
    [InlineData("<B>Foo</B>", "html")]
    [InlineData("Foo<br/>Bar", "html")]
    [InlineData("<b>Foo</b>", "html")]
    [InlineData("Foo<br>Bar", "html")]
    [InlineData("Fish &amp; chips", "html")]
    [InlineData("<a href=\"https://example.com\">link</a>", "html")]
    public void ToMicrosoftBody_ChoosesContentTypeAndLeavesContentUnchanged(string description, string expectedContentType)
    {
        var (contentType, content) = EventBodyHelper.ToMicrosoftBody(description);

        Assert.Equal(expectedContentType, contentType);
        Assert.Equal(description, content);
    }

    [Fact]
    public void ToMicrosoftBody_WritesMissingDescriptionAsEmptyText()
    {
        Assert.Equal(("text", string.Empty), EventBodyHelper.ToMicrosoftBody(null));
    }
}
