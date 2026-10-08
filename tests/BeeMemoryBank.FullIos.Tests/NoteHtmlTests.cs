using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>A note's page: Markdown rendered on the phone, no HTML or script of the note run, nothing loaded from anywhere.</summary>
public class NoteHtmlTests
{
    [Fact]
    public void Markdown_IsRendered()
    {
        var html = NoteHtml.Render("# Title\n\n**bold** and `code`\n\n- [x] done");
        html.Should().Contain("<h1").And.Contain("<strong>bold</strong>").And.Contain("<code>code</code>").And.Contain("type=\"checkbox\"");
    }

    [Fact]
    public void HtmlInANote_IsShownAsText_NeverRun()
    {
        var html = NoteHtml.Render("<script>alert(1)</script>\n\n<img src=x onerror=alert(2)>");
        html.Should().NotContain("<script>alert").And.NotContain("<img src=x");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void ThePage_MayLoadNothing_NoScript_NoRemotePicture()
    {
        var html = NoteHtml.Render("![tracker](https://example.com/pixel.png)");
        html.Should().Contain($"<meta http-equiv=\"Content-Security-Policy\" content=\"{NoteHtml.ContentSecurityPolicy}\">");
        NoteHtml.ContentSecurityPolicy.Should().StartWith("default-src 'none'").And.NotContain("script-src").And.Contain("img-src data:");
        NoteHtml.ContentSecurityPolicy.Should().NotContain("http");
    }

    [Fact]
    public void AMessagePage_IsEscaped()
    {
        NoteHtml.Text("<b>locked</b>").Should().Contain("&lt;b&gt;locked&lt;/b&gt;");
    }
}
