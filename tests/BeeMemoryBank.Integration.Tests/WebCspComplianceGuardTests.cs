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
        // Matches javascript: in href or other attributes
        var jsUrlRegex = new Regex(@"(?i)href\s*=\s*[""']?javascript:");
        var violations = new List<string>();

        foreach (var file in RazorAndHtmlFiles())
        {
            var content = File.ReadAllText(file);
            var matches = jsUrlRegex.Matches(content);
            foreach (Match m in matches)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                violations.Add($"{relativePath}: javascript: URL found");
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
}
