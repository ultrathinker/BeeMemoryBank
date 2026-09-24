using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Guard test: Ensures the Web UI contains ZERO inline JavaScript and ZERO inline event handlers
/// so that Content-Security-Policy can enforce script-src 'self' without 'unsafe-inline'.
/// </summary>
public class WebCspComplianceGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    public static readonly Regex JavascriptUrlRegex = new(
        @"(?i)\b(?:href|src|action|formaction|xlink:href)\s*=\s*[""']?\s*javascript:",
        RegexOptions.Compiled);

    public static readonly Regex InlineHandlerInMarkupRegex = new(
        @"(?i)<[a-z][a-z0-9-]*\b[^>]*?\bon[a-z-]+\s*=",
        RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> RazorAndHtmlFiles()
    {
        var pagesDir = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "Pages");
        if (Directory.Exists(pagesDir))
        {
            foreach (var f in Directory.EnumerateFiles(pagesDir, "*.cshtml", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(f);
                if (full.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    full.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                yield return f;
            }
        }

        var wwwrootDir = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot");
        if (Directory.Exists(wwwrootDir))
        {
            var libDir = Path.GetFullPath(Path.Combine(wwwrootDir, "lib")) + Path.DirectorySeparatorChar;
            foreach (var f in Directory.EnumerateFiles(wwwrootDir, "*.html", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(f);
                if (full.StartsWith(libDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return f;
            }
        }
    }

    private static IEnumerable<string> JavaScriptFiles()
    {
        var wwwrootDir = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot");
        if (Directory.Exists(wwwrootDir))
        {
            var libDir = Path.GetFullPath(Path.Combine(wwwrootDir, "lib")) + Path.DirectorySeparatorChar;
            foreach (var f in Directory.EnumerateFiles(wwwrootDir, "*.js", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(f);
                if (full.StartsWith(libDir, StringComparison.OrdinalIgnoreCase) ||
                    full.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    full.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                yield return f;
            }
        }
    }

    [Fact]
    public void RazorPages_ContainNoInlineExecutableScripts()
    {
        // Any <script> tag must have src attribute or type="application/json"
        var scriptTagRegex = new Regex(@"<script\b(?![^>]*\bsrc\b)(?![^>]*\btype=[""']application/json[""'])[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var violations = new List<string>();

        foreach (var file in RazorAndHtmlFiles())
        {
            var content = File.ReadAllText(file);
            var matches = scriptTagRegex.Matches(content);
            foreach (Match m in matches)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                violations.Add($"{relativePath}: inline executable script found ({m.Value.Substring(0, Math.Min(60, m.Value.Length))}...)");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void RazorPages_ContainNoInlineEventHandlers()
    {
        // Matches on* attributes including hyphenated Shoelace events like onsl-change=, onsl-request-close=, onclick=, etc.
        var onHandlerRegex = new Regex(@"(?i)\bon[a-z-]+\s*=");
        var violations = new List<string>();

        foreach (var file in RazorAndHtmlFiles())
        {
            var content = File.ReadAllText(file);
            var matches = onHandlerRegex.Matches(content);
            foreach (Match m in matches)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                violations.Add($"{relativePath}: inline event handler '{m.Value}'");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void RazorPages_ContainNoJavascriptUrls()
    {
        var violations = new List<string>();

        foreach (var file in RazorAndHtmlFiles())
        {
            var content = File.ReadAllText(file);
            var matches = JavascriptUrlRegex.Matches(content);
            foreach (Match m in matches)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                violations.Add($"{relativePath}: javascript: URL found ({m.Value})");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void JavaScriptFiles_ContainNoInlineEventHandlersInMarkup()
    {
        var violations = new List<string>();

        foreach (var file in JavaScriptFiles())
        {
            var content = File.ReadAllText(file);
            var matches = InlineHandlerInMarkupRegex.Matches(content);
            foreach (Match m in matches)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                violations.Add($"{relativePath}: inline event handler in markup found ({m.Value})");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void JavaScriptFiles_ContainNoJavascriptUrls()
    {
        var violations = new List<string>();

        foreach (var file in JavaScriptFiles())
        {
            var content = File.ReadAllText(file);
            var matches = JavascriptUrlRegex.Matches(content);
            foreach (Match m in matches)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                violations.Add($"{relativePath}: javascript: URL found ({m.Value})");
            }
        }

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("<sl-switch onsl-change=\"handleChange()\">")]
    [InlineData("<sl-dialog onsl-request-close=\"preventClose()\">")]
    [InlineData("<sl-button onsl-click=\"click()\">")]
    [InlineData("<button onclick=\"run()\">")]
    [InlineData("<form onsubmit=\"return false;\">")]
    public void InlineEventHandlerRegex_DetectsHyphenatedAndStandardEvents(string snippet)
    {
        var regex = new Regex(@"(?i)\bon[a-z-]+\s*=");
        Assert.Matches(regex, snippet);
    }

    [Theory]
    [InlineData("<sl-icon-button onclick=\"deleteKey()\">")]
    [InlineData("<button onsl-click=\"run()\">")]
    [InlineData("<div onmouseenter=\"show()\">")]
    [InlineData("<form onsubmit=\"return false;\">")]
    [InlineData("<custom-element onsl-change=\"test()\">")]
    [InlineData("<a href=\"#\" onclick=\"click()\">")]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\">")]
    [InlineData("<body onload=\"init()\">")]
    public void InlineHandlerInMarkupRegex_DetectsHits(string snippet)
    {
        Assert.Matches(InlineHandlerInMarkupRegex, snippet);
    }

    [Theory]
    [InlineData("reader.onload = function () { }")]
    [InlineData("reader.onerror = function () { }")]
    [InlineData("input.onchange = function () { }")]
    [InlineData("newScript.onload = resolve;")]
    [InlineData("var onTransitionEnd = function (e) { };")]
    [InlineData("state.onChange = opts.onChange;")]
    [InlineData("var onThisFolder = location.pathname === '/Folder';")]
    [InlineData("<div><span>Normal content</span></div>")]
    [InlineData("<sl-button variant=\"primary\">Submit</sl-button>")]
    public void InlineHandlerInMarkupRegex_RejectsMisses(string snippet)
    {
        Assert.DoesNotMatch(InlineHandlerInMarkupRegex, snippet);
    }

    [Theory]
    [InlineData("<a href=\"javascript:void(0)\">")]
    [InlineData("<a href='javascript:alert(1)'>")]
    [InlineData("<a href=javascript:alert(1)>")]
    [InlineData("<a href=\"  javascript:alert(1)\">")]
    [InlineData("<a HREF=\"javascript:alert(1)\">")]
    [InlineData("<iframe src=\"javascript:evil()\">")]
    [InlineData("<form action=\"javascript:send()\">")]
    [InlineData("<button formaction=\"javascript:del()\">")]
    [InlineData("<image xlink:href=\"javascript:svg()\">")]
    [InlineData("action = \"javascript:submit()\"")]
    [InlineData("formaction = ' javascript:exec()'")]
    public void JavascriptUrlRegex_DetectsHits(string snippet)
    {
        Assert.Matches(JavascriptUrlRegex, snippet);
    }

    [Theory]
    [InlineData("<a href=\"/Article/View?id=123\">")]
    [InlineData("<script src=\"/js/site.js\"></script>")]
    [InlineData("<form action=\"/Admin?handler=Save\">")]
    [InlineData("<button formaction=\"/Delete\">")]
    [InlineData("<use xlink:href=\"#icon-symbol\"></use>")]
    [InlineData("// This comment mentions javascript: protocol")]
    [InlineData("var lang = 'javascript';")]
    [InlineData("isSpaUrl('/Article/View')")]
    public void JavascriptUrlRegex_RejectsMisses(string snippet)
    {
        Assert.DoesNotMatch(JavascriptUrlRegex, snippet);
    }
}
