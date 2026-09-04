using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Refuses a string literal that is left open at the end of a line inside inline JavaScript.
///
/// <para>
/// A real newline inside a single- or double-quoted JS string is a SyntaxError, and the browser's
/// response to one is to discard the <c>&lt;script&gt;</c> block <b>whole</b> — not just the
/// statement. That happened in <c>Admin.cshtml</c>: a two-line confirmation message written across
/// a real line break took 320 lines of the page's JavaScript down with it, so the demote button,
/// both auto-accept toggles, change-URL and every snapshot control silently stopped working. There
/// was no compiler error, no server-side log line, and no failing test — Razor is happy to emit
/// broken JavaScript, and the server never parses what it serves.
/// </para>
///
/// <para>
/// This is deliberately a small hand-written scanner rather than a shell-out to <c>node --check</c>.
/// The CI image has no Node, so a test built on that would skip in exactly the place it needs to
/// run, and a test that skips where it matters is worse than none. The scanner answers one narrow
/// question — is a quote still open when the line ends? — which is the whole of this bug class.
/// </para>
///
/// <para>
/// It has to understand four constructs to answer that without lying: line comments, block
/// comments, template literals, and regex literals. The last is not optional — this codebase
/// escapes HTML with <c>.replace(/"/g, "&amp;quot;")</c> in five places, and a scanner that cannot
/// tell a regex from a division reads that quote as an opening one and reports every one of them.
/// A <c>/</c> opens a regex when the last meaningful character before it cannot end an expression;
/// after an identifier, a digit, <c>)</c> or <c>]</c> it is division instead.
/// </para>
/// </summary>
public class InlineScriptSyntaxGuardTests
{
    private static readonly string[] SearchRoots = ["server", "desktop"];

    /// <summary>Inline blocks only — a <c>src=</c> tag has no body to check.</summary>
    private static readonly Regex ScriptBlock = new(
        @"<script(?![^>]*\bsrc\s*=)[^>]*>(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A Razor transition (<c>@Html</c>, <c>@Model</c>, <c>@if</c>, <c>@{</c>, …). The scanner reads
    /// its input as JavaScript, and Razor expressions are not that — a block containing one is
    /// skipped rather than guessed at. Reported as a count so it stays visible how much is NOT
    /// covered.
    /// </summary>
    private static readonly Regex RazorTransition = new(@"@[A-Za-z_{(]", RegexOptions.Compiled);

    [Fact]
    public void InlineScripts_HaveNoStringLiteralLeftOpenAtEndOfLine()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();
        int blocksChecked = 0, blocksSkipped = 0;

        foreach (var file in EnumerateSources(repoRoot))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            // A .js file is one implicit block; a .cshtml file is every <script> tag in it.
            var blocks = file.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? [(Body: text, Offset: 0)]
                : ScriptBlock.Matches(text)
                    .Select(m => (Body: m.Groups[1].Value, Offset: m.Groups[1].Index))
                    .ToArray();

            foreach (var (body, offset) in blocks)
            {
                if (RazorTransition.IsMatch(body))
                {
                    blocksSkipped++;
                    continue;
                }

                blocksChecked++;
                var startLine = LineOf(text, offset);

                foreach (var lineNo in UnterminatedStringLines(body))
                    offenders.Add($"{relative}:{startLine + lineNo - 1}");
            }
        }

        blocksChecked.Should().BeGreaterThan(10,
            "the scanner must actually be reading something — if the roots or the pattern stop " +
            "matching, an empty result would look like a pass forever");

        offenders.Should().BeEmpty(
            "a newline inside a quoted JS string is a SyntaxError, and the browser discards the " +
            "whole <script> block on one — silently, with no server-side symptom. Write the break " +
            "as an escape sequence instead. Offending lines:\n  " + string.Join("\n  ", offenders) +
            $"\n({blocksChecked} blocks checked, {blocksSkipped} skipped for containing Razor)");
    }

    /// <summary>
    /// Returns the 1-based line numbers within <paramref name="body"/> that end while a single- or
    /// double-quoted string is still open. Backtick strings legally span lines and are tracked only
    /// so their contents cannot be mistaken for anything else.
    /// </summary>
    private static IEnumerable<int> UnterminatedStringLines(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var inBlockComment = false;
        var inTemplate = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            char quote = '\0';              // '\0' = not inside a '…' or "…" string
            char lastSignificant = '\0';    // drives the regex-versus-division decision

            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                var next = c + 1 < line.Length ? line[c + 1] : '\0';

                if (inBlockComment)
                {
                    if (ch == '*' && next == '/') { inBlockComment = false; c++; }
                    continue;
                }

                if (inTemplate)
                {
                    if (ch == '\\') { c++; continue; }
                    if (ch == '`') inTemplate = false;
                    continue;
                }

                if (quote != '\0')
                {
                    if (ch == '\\') { c++; continue; }   // escaped anything, including the quote
                    if (ch == quote) quote = '\0';
                    continue;
                }

                // Outside every string and comment.
                if (ch == '/' && next == '/') break;                  // rest of the line is a comment
                if (ch == '/' && next == '*') { inBlockComment = true; c++; continue; }

                if (ch == '/' && StartsRegex(lastSignificant))
                {
                    c = SkipRegexLiteral(line, c);
                    lastSignificant = '/';
                    continue;
                }

                if (ch == '`') { inTemplate = true; lastSignificant = '`'; continue; }
                if (ch == '\'' || ch == '"') { quote = ch; continue; }

                if (!char.IsWhiteSpace(ch)) lastSignificant = ch;
            }

            // A '…' or "…" still open here ran into the newline. That is the bug.
            if (quote != '\0')
                yield return i + 1;
        }
    }

    /// <summary>
    /// Whether a <c>/</c> following <paramref name="lastSignificant"/> opens a regex literal rather
    /// than being the division operator. A regex may only appear where a value is expected, so the
    /// test is on what came before: an identifier character, a digit, <c>)</c> or <c>]</c> ends an
    /// expression and makes the slash division; anything else — an operator, a comma, an opening
    /// bracket, or the start of the line — means a value is expected and the slash opens a regex.
    /// </summary>
    private static bool StartsRegex(char lastSignificant) =>
        !(char.IsLetterOrDigit(lastSignificant) || lastSignificant == '_' || lastSignificant == '$'
          || lastSignificant == ')' || lastSignificant == ']');

    /// <summary>
    /// Returns the index of the regex literal's closing <c>/</c>, given the index of its opening
    /// one. A <c>/</c> inside a character class does not close the literal, so those are tracked,
    /// and a backslash escapes the next character anywhere. An unterminated literal (impossible in
    /// valid JS, since a regex cannot span lines) yields the end of the line, which simply ends the
    /// scan of that line rather than reporting a string that was never open.
    /// </summary>
    private static int SkipRegexLiteral(string line, int start)
    {
        var inClass = false;
        for (var i = start + 1; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\\') { i++; continue; }
            if (inClass) { if (ch == ']') inClass = false; continue; }
            if (ch == '[') { inClass = true; continue; }
            if (ch == '/') return i;
        }
        return line.Length;
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (var root in SearchRoots)
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".cshtml", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Build output and vendored libraries are not ours to police, and a minified
                // bundle would drown the result either way.
                var p = file.Replace('\\', '/');
                if (p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("/lib/") ||
                    p.Contains("/node_modules/") || p.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return file;
            }
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

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

        throw new InvalidOperationException($"Could not locate the repo root from {AppContext.BaseDirectory}");
    }
}
