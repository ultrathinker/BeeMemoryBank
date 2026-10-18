using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// BMB-306/307 fix round 3 (G3): the words of an exception that came from a call to another device - its message carries the address that was dialled, a token in a
/// query, the reason phrase and the page a proxy sent - never reach a log line, a status or a screen. The ONE mechanism is <c>FailureText</c> / <c>PeerContactLog.Describe</c>
/// (the kind of failure from a closed set, chosen by the TYPE of the exception). This test is what keeps it so in the projects that call other devices or show what happened to
/// the person who pressed a button: it reads the sources of the networking projects and fails on
/// <list type="bullet">
/// <item>an exception's <c>.Message</c> inside a <c>Log*(...)</c> call or the phone's app-log call (<c>log.Add(...)</c>);</item>
/// <item>an exception's <c>.Message</c> assigned to a status or error member (<c>Error = ...</c>, <c>Detail = ...</c>, <c>Reason = ...</c>);</item>
/// <item>the <c>.Message</c> of a caught exception anywhere in the service folders of the web and the phone apps, whose text a person reads.</item>
/// </list>
/// A place that has a reason to keep an exception's text - local work, with no call to another device in it - is in <see cref="Local"/> with that reason, and a place that is
/// in the list and no longer needs it fails too. The sweep that lists EVERY use of <c>.Message</c> in the tree is <c>ExceptionTextSitesInventoryTests</c>; this one is stricter
/// where it matters.
/// </summary>
public class NetworkFailureTextArchitectureTests
{
    private static readonly string[] Areas =
    [
        "libs/BeeMemoryBank.Sync",
        "libs/BeeMemoryBank.Blind.PhoneClient",
        "libs/BeeMemoryBank.Blind.AppCore",
        "server/BeeMemoryBank.Api/Services/Blind",
        "server/BeeMemoryBank.Api/Services/BlindBackup",
        "server/BeeMemoryBank.Web/Services",
        "mobile/BeeMemoryBank.Mobile/Services",
    ];

    /// <summary>The folders whose text a person reads: any caught exception's message in them is a finding.</summary>
    private static readonly string[] ScreenAreas = ["server/BeeMemoryBank.Web/Services", "mobile/BeeMemoryBank.Mobile/Services"];

    private sealed record Allowed(string File, string Snippet, string Reason);

    /// <summary>Places that keep the text of an exception, and why that is safe: the failure is local work and no call to another device is in it.</summary>
    private static readonly Allowed[] Local =
    [
        new("libs/BeeMemoryBank.Sync/PendingEmbeddingProcessor.cs", "Embedding model is unavailable",
            "the embedding model is a file this node loads from its own disk; no peer is involved"),
        new("libs/BeeMemoryBank.Blind.AppCore/Core/Services/BlindPhone/BlindPhoneBackupRunner.cs", "Backup failed:",
            "an IOException, InvalidDataException or UnauthorizedAccessException of the phone's own backup files; the backup makes no call to another device"),
        new("libs/BeeMemoryBank.Blind.AppCore/Services/Blind/BlindAppController.cs", "Could not set the app up",
            "the app's own start: its files and its database on the phone"),
        new("server/BeeMemoryBank.Api/Services/Blind/BaselineProducer.cs", "Older produced baselines could not be removed",
            "an IOException or UnauthorizedAccessException on this node's own baseline folder"),
        new("server/BeeMemoryBank.Api/Services/Blind/BaselineScheduler.cs", "Baseline scheduler tick failed",
            "producing a baseline is local work on this node's database and media; the tick calls no other device"),
        new("server/BeeMemoryBank.Api/Services/Blind/BaselineScheduler.cs", "was not produced",
            "producing a baseline is local work on this node's database and media; it calls no other device"),
        new("server/BeeMemoryBank.Api/Services/Blind/BlindNodeManager.cs", "is not reseeded yet",
            "BaselineNotReadyException is this code's own typed refusal and carries a sentence written here"),
        new("server/BeeMemoryBank.Api/Services/Blind/FullNodeBaselineSource.cs", "cannot be read and is not served",
            "an IOException on a file of this node's own baseline folder"),
        new("mobile/BeeMemoryBank.Mobile/Services/SyncStatusService.cs", "SyncStatusService.RefreshAsync error",
            "Debug.WriteLine: the debugger's output window only, not a log, a status or a screen"),
        new("mobile/BeeMemoryBank.Mobile/Services/SyncStatusService.cs", "SyncNowAsync error",
            "Debug.WriteLine: the debugger's output window only, not a log, a status or a screen"),
    ];

    private static readonly Regex LogCall = new(
        @"\b(?:_?logger|log)\s*\??\.\s*Log\w*\s*\(|\bLog(?:Warning|Error|Information|Debug|Critical|Trace)\s*\(|\blog\s*\.\s*Add\s*\(", RegexOptions.Compiled);
    private static readonly Regex StatusMember = new(
        @"\b(Error|Reason|LastError|Status\w*|Detail\w*|Message|ErrorMessage|Problem|Why|Text|Cause)\s*=\s*(?![=>])", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new("\"(?:\\\\.|[^\"\\\\\\n])*\"", RegexOptions.Compiled);
    private static readonly Regex CaughtMessage = new(@"\b(?:ex|e|err|error|exception|inner|failure)\.Message\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    private static string Code(string path) =>
        string.Join('\n', File.ReadAllText(path).Split('\n').Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 && !l.Contains("http", StringComparison.Ordinal) ? l[..i] : l; }));

    private static bool InsideLogCall(string code, int at)
    {
        var from = Math.Max(0, at - 900);
        Match? last = null;
        foreach (var match in LogCall.Matches(code[from..at]).Cast<Match>()) last = match;
        if (last is null) return false;
        var segment = StringLiteral.Replace(code[(from + last.Index + last.Length)..at], "\"\"");
        return !segment.Contains(';') && 1 + segment.Count(c => c == '(') - segment.Count(c => c == ')') >= 1;
    }

    private sealed record Finding(string File, string Kind, string Statement);

    private static List<Finding> Findings(string root)
    {
        var found = new List<Finding>();
        foreach (var area in Areas)
        {
            var folder = Path.Combine(root, area);
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
                var code = Code(path);
                foreach (var message in Regex.Matches(code, @"\.Message\b").Cast<Match>())
                {
                    var statementStart = code.LastIndexOf(';', message.Index, Math.Min(message.Index, 500)) + 1;
                    var statement = string.Join(' ', code[statementStart..Math.Min(code.Length, message.Index + 12)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    string? kind = null;
                    if (InsideLogCall(code, message.Index)) kind = "log";
                    else if (StatusMember.IsMatch(StringLiteral.Replace(code[statementStart..Math.Min(code.Length, message.Index + 12)], "\"\""))) kind = "status";
                    else if (ScreenAreas.Any(a => relative.StartsWith(a + "/", StringComparison.Ordinal))
                        && CaughtMessage.IsMatch(code.Substring(Math.Max(0, message.Index - 12), Math.Min(code.Length - Math.Max(0, message.Index - 12), 24)))) kind = "screen";
                    if (kind is not null) found.Add(new Finding(relative, kind, statement));
                }
            }
        }
        return found;
    }

    [Fact]
    public void NoExceptionTextReachesALogAStatusOrAScreenOfTheNetworkingProjectsExceptWhereTheFailureIsLocalWork()
    {
        var found = Findings(RepoRoot());

        var unexplained = found.Where(f => !Local.Any(a => a.File == f.File && f.Statement.Contains(a.Snippet, StringComparison.Ordinal)))
            .Select(f => $"{f.File} ({f.Kind}): {(f.Statement.Length > 160 ? f.Statement[^160..] : f.Statement)}").ToList();
        unexplained.Should().BeEmpty(
            "the message of an exception that can come from a call to another device carries an address, a token and the other side's words: say the kind of failure instead "
            + "(FailureText.Say / PeerContactLog.Describe / FailureText.Story), or - if the failure is local work - add the place to Local with the reason");

        var stale = Local.Where(a => !found.Any(f => f.File == a.File && f.Statement.Contains(a.Snippet, StringComparison.Ordinal))).Select(a => $"{a.File}: {a.Snippet}").ToList();
        stale.Should().BeEmpty("an allowed place that no longer keeps an exception's text is removed from the list");
        Local.Should().OnlyContain(a => a.Reason.Length > 30, "every allowed place says why");
    }

    [Fact]
    public void TheFourPlacesTheReviewNamedUseTheOneMapper()
    {
        var root = RepoRoot();
        string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

        var scheduler = Read("libs/BeeMemoryBank.Sync/SyncScheduler.cs");
        var note = scheduler[scheduler.IndexOf("private void NoteUnreachable", StringComparison.Ordinal)..];
        note[..note.IndexOf("public override void Dispose", StringComparison.Ordinal)].Should().Contain("PeerContactLog.Describe(ex)").And.NotContain("ex.Message");

        Read("server/BeeMemoryBank.Api/Services/Blind/BlindNodeManager.cs").Should().Contain("FailureText.Say(ex)");
        var lan = Read("server/BeeMemoryBank.Web/Services/NodeLanClient.cs");
        lan.Should().Contain("FailureText.Say(ex)").And.NotContain("ex.Message");
        var busy = Read("mobile/BeeMemoryBank.Mobile/Services/BusyAction.cs");
        busy.Should().Contain("FailureText.Story(ex)");
        Regex.IsMatch(busy, @"\b(?:ex|e|inner)\.Message\b").Should().BeFalse("the generic busy helper reads no exception's message itself: FailureText.Story tells the kinds apart by type");
    }
}
