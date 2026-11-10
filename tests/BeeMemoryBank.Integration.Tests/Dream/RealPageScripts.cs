using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The scripts of a page in the order the browser runs them. The layout writes the page's body where it says <c>@RenderBody()</c>, its own scripts after it
/// (bmb-busy.js, the site scripts) and the page's <c>@section Scripts</c> last: so the scripts a page lists in its own body run BEFORE the layout's. The node
/// harnesses of the chat page are given the order of the real page, so they cannot run the scripts in an order the page never has; the order is read from the
/// markup of the page and the layout, and a test checks it against the rendered page.
/// </summary>
internal static class RealPageScripts
{
    private static readonly Regex Tag = new(@"<script\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Src = new(@"\bsrc\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Later = new(@"\btype\s*=\s*""module""|\bdefer\b|\basync\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The <c>src</c> of every script of the HTML (without the <c>?v=</c> of the cache), as the browser runs them: classic scripts in the order of the document, then the modules and deferred ones.</summary>
    internal static List<string> OrderOf(string html)
    {
        List<string> now = [], later = [];
        foreach (Match tag in Tag.Matches(RazorComment.Replace(html, "")))
        {
            var src = Src.Match(tag.Groups[1].Value);
            if (!src.Success) continue;
            var path = src.Groups[1].Value;
            var query = path.IndexOf('?');
            if (query >= 0) path = path[..query];
            (Later.IsMatch(tag.Groups[1].Value) ? later : now).Add(path);
        }
        return [.. now, .. later];
    }

    /// <summary>A page's markup cut into what it puts in the body and what is in its <c>@section Scripts { ... }</c> (empty when it has none).</summary>
    internal static (string Body, string Scripts) SplitScriptsSection(string page)
    {
        var at = page.IndexOf("@section Scripts", StringComparison.Ordinal);
        if (at < 0) return (page, "");
        var open = page.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < page.Length; i++)
        {
            if (page[i] == '{') depth++;
            else if (page[i] == '}' && --depth == 0)
                return (page[..at] + page[(i + 1)..], page[(open + 1)..i]);
        }
        throw new InvalidOperationException("the Scripts section of the page does not end");
    }

    /// <summary>The order of the page, made from its markup and the layout's, without rendering: head, the page's body, the rest of the layout, the page's Scripts section.</summary>
    internal static List<string> FromSources(string pageMarkup, string layoutMarkup)
    {
        const string body = "@RenderBody()";
        const string section = "@await RenderSectionAsync(\"Scripts\"";
        var bodyAt = layoutMarkup.IndexOf(body, StringComparison.Ordinal);
        var sectionAt = layoutMarkup.IndexOf(section, StringComparison.Ordinal);
        if (bodyAt < 0 || sectionAt < bodyAt) throw new InvalidOperationException("the layout no longer has RenderBody and then the Scripts section");
        var (pageBody, pageScripts) = SplitScriptsSection(pageMarkup);
        return OrderOf(layoutMarkup[..bodyAt] + pageBody + layoutMarkup[(bodyAt + body.Length)..sectionAt] + pageScripts + layoutMarkup[sectionAt..]);
    }
}
