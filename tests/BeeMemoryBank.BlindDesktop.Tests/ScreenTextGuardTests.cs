using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// A guard over the SOURCE of the desktop heads: nothing reads the message of an exception into text - not into something a person sees (a notice, a label, a tooltip), and not into a diagnostic either
/// (the error log file, the console output of the headless self-check). The kind of failure is told on a screen by <c>FailureNotice.Say</c> (<c>BlindFailureText</c>); a diagnostic gets the exception itself
/// and records its type and frames (<c>DiagnosticText.Describe</c>). A line that reads <c>ex.Message</c> or <c>ex.ToString()</c> (or the same of any variable a <c>catch</c> declares) fails this test unless the
/// file is one of the few that read a message for something else than text (listed below with the reason).
/// </summary>
public sealed class ScreenTextGuardTests
{
    private static readonly string[] Projects = ["BeeMemoryBank.BlindDesktop", "BeeMemoryBank.BlindDesktop.Windows", "BeeMemoryBank.BlindDesktop.MacOS"];

    /// <summary>Files that read an exception's message and do not turn it into a screen's text or a log's text: they classify it, or carry it inside another exception that a screen tells by its kind.</summary>
    private static readonly Dictionary<string, string> NotAScreen = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BeeMemoryBank.BlindDesktop.MacOS/Instance/FileLockSingleInstance.cs"] = "classifies an IOException of the lock file (is another process holding it); the text is not kept",
        ["BeeMemoryBank.BlindDesktop.MacOS/Storage/MacOsBlindStateStore.cs"] = "text of an AggregateException, which a screen tells by its kind and a log by its type and frames",
    };

    private static readonly Regex CatchVariable = new(@"catch\s*\(\s*[\w.]+\s+(\w+)\s*\)", RegexOptions.Compiled);

    /// <summary>The lines of <paramref name="source"/> that read an exception's message (or turn it into text), in a log call as well as anywhere else. Comments are not code.</summary>
    internal static IReadOnlyList<(int Line, string Text)> Offences(string source)
    {
        var names = new HashSet<string>(["ex", "exception", "inner"], StringComparer.Ordinal);
        foreach (Match match in CatchVariable.Matches(source)) names.Add(match.Groups[1].Value);
        var any = string.Join("|", names.Select(Regex.Escape));
        // ex.Message, ex.ToString(), and the whole exception put into an interpolated string ({ex}), which is its ToString() too.
        var read = new Regex(@"\b(?:" + any + @")\??\.(?:Message\b|ToString\s*\()|\{\s*(?:" + any + @")\s*[:,}]");

        var found = new List<(int, string)>();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var code = lines[i];
            var comment = code.IndexOf("//", StringComparison.Ordinal);
            if (code.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (comment >= 0 && !code[..comment].Contains('"')) code = code[..comment];
            if (!read.IsMatch(code)) continue;
            found.Add((i + 1, lines[i].Trim()));
        }
        return found;
    }

    private static IEnumerable<(string Key, string Path)> SourceFiles()
    {
        var desktop = Path.Combine(VaultBoundary.RepoRoot(), "desktop");
        foreach (var project in Projects)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(desktop, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Path.Combine(desktop, project), file).Replace('\\', '/');
                if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
                yield return ($"{project}/{relative}", file);
            }
    }

    [Fact]
    public void NoScreenOfTheDesktopHeads_ReadsTheMessageOfAnException()
    {
        var files = SourceFiles().ToList();
        files.Should().Contain(f => f.Key == "BeeMemoryBank.BlindDesktop/ViewModels/AddAccountViewModel.cs", "the scan finds the view models");
        files.Should().Contain(f => f.Key.StartsWith("BeeMemoryBank.BlindDesktop.Windows/", StringComparison.Ordinal));
        files.Should().Contain(f => f.Key.StartsWith("BeeMemoryBank.BlindDesktop.MacOS/", StringComparison.Ordinal));

        var offences = files
            .Where(f => !NotAScreen.ContainsKey(f.Key))
            .SelectMany(f => Offences(File.ReadAllText(f.Path)).Select(o => $"{f.Key}:{o.Line}: {o.Text}"))
            .ToList();

        offences.Should().BeEmpty("a screen tells the KIND of failure (FailureNotice.Say / BlindFailureText) and a log records the exception's type and frames (DiagnosticText.Describe); the message can carry a path, an address or a token");
    }

    [Fact]
    public void TheFilesThatMayReadAMessage_AreNotScreens_AndStillNeedTheirEntry()
    {
        foreach (var key in NotAScreen.Keys)
        {
            key.Should().NotContain("/ViewModels/").And.NotContain("/Views/").And.NotContain("/Services/").And.NotEndWith("BlindDesktopShell.cs", "a screen is never on the list");
            var path = SourceFiles().SingleOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).Path;
            path.Should().NotBeNull($"{key} is listed and must exist");
            Offences(File.ReadAllText(path!)).Should().NotBeEmpty($"{key} no longer reads an exception message: take it off the list");
        }
    }

    [Fact]
    public void Control_TheScanFindsAMessageThatGoesToANotice_OrToALog_AndAnyVariableACatchDeclares()
    {
        var planted = """
            try { Work(); }
            catch (IOException ex) { Notice = $"That did not work: {ex.Message}"; }
            try { Work(); }
            catch (Exception failure) { Error = failure.Message; }
            try { Work(); }
            catch (Exception ex) { ErrorLog.Write($"It failed: {ex.Message}", ex); }
            try { Work(); }
            catch (Exception ex) { Console.Error.WriteLine($"[self-check] {ex.ToString()}"); }
            try { Work(); }
            catch (Exception ex) { Console.Error.WriteLine($"[self-check] it failed: {ex}"); }
            """;

        Offences(planted).Select(o => o.Line).Should().Equal(2, 4, 6, 8, 10);
    }

    [Fact]
    public void Control_TheScanLeavesAnExceptionPassedToALog_AndAComment_Alone()
    {
        var allowed = """
            catch (Exception ex) { ErrorLog.Write("It failed", ex); _problem?.Invoke("what", ex); }
            catch (Exception ex) { Console.Error.WriteLine($"[self-check] {DiagnosticText.Describe(ex)}"); }
            // Notice = ex.Message would be wrong here
            var text = confirmation.Message;
            """;

        Offences(allowed).Should().BeEmpty();
    }
}
