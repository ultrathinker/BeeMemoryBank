using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 3, the stored text is not a closed set (review MAJOR-2): what a stored line may say is decided by WHERE IT CAME FROM, never by what it looks like. A line of the phone's log written by
/// this version carries the stamp <c>v</c> (<see cref="BlindFailureText.StoredVersion"/>); a line without it (everything 2.6.4 wrote) shows only its time, its kind and the fixed sentence; the
/// <c>pairing</c> kind is no exception unless the line is stamped and has the exact shape this code writes. The hostile lines are the ones of the review: they carry no path marker, so the cheap
/// filter alone lets them through.
/// </summary>
public sealed class BlindStoredLineProvenanceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Node = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-tests", Guid.NewGuid().ToString("N"));

    public BlindStoredLineProvenanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The words that exist only in the hostile lines; none of them has a path separator, an address or the prefix of a key alias.</summary>
    private static readonly string[] Needles = ["filePath", "token=", "hunter2", "8f3a-canary-123", "seed-alias-xq7", "secret", @"C:\"];

    private static bool Leaks(string? text) => text is not null && Needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hostile messages that the cheap location check does NOT catch (no slash, no backslash, no "://", no "bmb_blind_").</summary>
    public static TheoryData<string> PlainHostileLines => new(
        "Invalid argument (Parameter 'filePath') token=hunter2 account 8f3a-canary-123",
        "Parameter 'filePath'",
        "token=hunter2",
        "account 8f3a-canary-123",
        "alias seed-alias-xq7 was rejected",
        "Backup failed: the secret of the account is hunter2");

    /// <summary>A line as 2.6.4 wrote it: the entry of that version had no stamp.</summary>
    private static string Legacy(string kind, string message, DateTimeOffset? at = null) => JsonSerializer.Serialize(new { At = at ?? At, Kind = kind, Message = message });

    private BlindPhoneLog LogOf(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return new BlindPhoneLog(path, new FixedClock());
    }

    private static BlindPhoneLog.Entry Stamped(string kind, string message) => new(At, kind, message);

    private static BlindPhoneLog.Entry Unstamped(string kind, string message) => new(At, kind, message, Version: 0);

    // ---- the log file --------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(PlainHostileLines))]
    public void TheCheapFilterAlone_LetsTheHostileLinesThrough_SoItCannotBeTheRule(string line) =>
        BlindFailureText.ForDisplay(line).Should().Be(line, "this is why a stored text is decided by its stamp and not by its look");

    [Theory]
    [MemberData(nameof(PlainHostileLines))]
    public void ALineOf264_WithAPlainHostileMessage_ShowsOnlyItsTimeItsKindAndTheFixedSentence(string line)
    {
        var path = Path.Combine(_dir, "old.jsonl");
        var raw = new[] { Legacy("sync", "Sync failed: " + line), Legacy("backup", line), Legacy("load", line) };
        File.WriteAllLines(path, raw);
        var before = File.ReadAllBytes(path);
        var log = new BlindPhoneLog(path, new FixedClock());

        var shown = log.Latest(10);

        shown.Select(e => e.Kind).Should().Equal("load", "backup", "sync");
        shown.Select(e => e.At).Should().OnlyContain(t => t == At);
        shown.Select(e => e.Message).Should().OnlyContain(m => m == BlindFailureText.StoredTextHidden);
        log.LatestStored(10).Select(e => e.Message).Should().Contain(line, "what is stored is not rewritten, and the code that compares a line it is about to write still sees it");
        File.ReadAllBytes(path).Should().Equal(before, "reading never rewrites the file");
    }

    [Fact]
    public void EveryLineOf264_ShowsTheFixedSentence_EvenOneThatLooksHarmless_AndTheOnePairingLineIsNoException()
    {
        var log = LogOf(
            Legacy("pairing", $"Paired: calls https://127.0.0.1:5610 (node {Node})."),
            Legacy("backup", "Backup made: bmb-phone-20261004-100800.bmbbackup (12 KiB)."),
            Legacy("load", "First load finished."));

        log.Latest(10).Should().OnlyContain(e => e.Message == BlindFailureText.StoredTextHidden);
        log.Latest(10).Select(e => e.Kind).Should().Equal("load", "backup", "pairing");
    }

    [Fact]
    public void ALineWithoutTheStamp_IsTheSameAsOneStampedWithAnOlderNumber()
    {
        var log = LogOf(JsonSerializer.Serialize(new { At, Kind = "sync", Message = "Sync failed: no answer: connection refused", v = 1 }), Legacy("sync", "x"));

        log.Latest(10).Should().OnlyContain(e => e.Message == BlindFailureText.StoredTextHidden);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(999)]
    public void ALineStampedWithAnUnknownFutureVersion_IsHiddenEvenWhenItsMessageHasNoLocationMarker(int version)
    {
        var log = LogOf(JsonSerializer.Serialize(new { At, Kind = "sync", Message = "token=hunter2", v = version }));

        log.Latest(1).Single().Message.Should().Be(BlindFailureText.StoredTextHidden);
        BlindFailureText.ForDisplay("token=hunter2", version).Should().Be(BlindFailureText.StoredTextHidden);
    }

    [Theory]
    [InlineData("{\"At\":\"2026-10-04T10:00:00+00:00\",\"Kind\":\"sync\",\"Message\":\"token=hunter2\",\"v\":1,\"v\":2}")]
    [InlineData("{\"At\":\"2026-10-04T10:00:00+00:00\",\"Kind\":\"sync\",\"Message\":\"token=hunter2\",\"v\":2,\"Future\":\"accepted-by-an-older-reader\"}")]
    public void ALineWithDuplicateOrUnknownJsonProperties_CannotClaimCurrentProvenance(string line)
    {
        var log = LogOf(line);

        log.Latest(1).Single().Message.Should().Be(BlindFailureText.StoredTextHidden);
        log.LatestStored(1).Single().Message.Should().Be("token=hunter2", "the stored line stays available to write-side comparisons without being displayable");
    }

    [Fact]
    public void ALineWrittenByThisVersion_IsStamped_AndShowsItsText()
    {
        var path = Path.Combine(_dir, "new.jsonl");
        var log = new BlindPhoneLog(path, new FixedClock());

        log.Add("sync", "Sync failed: no answer: connection refused");
        log.Add("pairing", $"Paired: calls https://127.0.0.1:5610 (node {Node}).");

        File.ReadAllLines(path).Should().OnlyContain(l => l.Contains("\"v\":" + BlindFailureText.StoredVersion));
        log.Latest(10).Select(e => e.Message).Should().Equal($"Paired: calls https://127.0.0.1:5610 (node {Node}).", "Sync failed: no answer: connection refused");
    }

    [Fact]
    public void AFileOfBothVersions_ShowsTheNewLinesAndHidesTheOldOnes_AndKeepsAllOfThem()
    {
        var path = Path.Combine(_dir, "mixed.jsonl");
        File.WriteAllLines(path, [Legacy("sync", "Sync failed: Parameter 'filePath' token=hunter2")]);
        var log = new BlindPhoneLog(path, new FixedClock());

        log.Add("sync", "Sync failed: no answer: connection refused");

        File.ReadAllLines(path).Should().HaveCount(2);
        log.Latest(10).Select(e => e.Message).Should().Equal("Sync failed: no answer: connection refused", BlindFailureText.StoredTextHidden);
    }

    [Fact]
    public void AStampedLine_StillGoesThroughTheCheapLocationCheck_AsASecondLayer()
    {
        var log = LogOf(JsonSerializer.Serialize(new { At, Kind = "sync", Message = @"Sync failed: C:\secret\x", v = BlindFailureText.StoredVersion }));

        log.Latest(1).Single().Message.Should().Be(BlindFailureText.StoredTextHidden);
    }

    // ---- the pairing kind is stored data too ---------------------------------------------------------------------------------------------------

    public static TheoryData<string> ForgedPairingMessages => new(
        @"Exception: C:\secret\bmb_blind_seed_v1 token=hunter2",
        "Paired: calls https://127.0.0.1:5610 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0). token=hunter2",
        "Paired: calls https://127.0.0.1:5610/api/sync?token=hunter2 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).",
        "Paired: calls https://user:hunter2@127.0.0.1:5610 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).",
        "Paired: calls http://127.0.0.1:5610 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).",
        "Paired: calls ftp://127.0.0.1 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).",
        "Paired: calls https://127.0.0.1:5610 (node account-8f3a-canary-123).",
        "Paired: calls not-an-address (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).",
        "Blind identity created: Parameter 'filePath'",
        "Blind identity created: 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 token=hunter2",
        "Recovered existing blind identity: seed-alias-xq7",
        "Re-pairing started: a new phone code was made. token=hunter2",
        "Cleaned up pairing secret committed in previous session. C:\\secret",
        "");

    [Theory]
    [MemberData(nameof(ForgedPairingMessages))]
    public void AStampedPairingLine_ThatIsNotExactlyOneThisCodeWrites_IsHidden(string message)
    {
        var log = LogOf(JsonSerializer.Serialize(Stamped("pairing", message)));

        log.Latest(1).Single().Message.Should().Be(BlindFailureText.StoredTextHidden);
    }

    [Theory]
    [InlineData("Re-pairing started: a new phone code was made.")]
    [InlineData("Cleaned up pairing secret committed in previous session.")]
    [InlineData("Blind identity created: 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")]
    [InlineData("Recovered existing blind identity: 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")]
    [InlineData("Paired: calls https://127.0.0.1:5610 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).")]
    [InlineData("Paired: calls https://hub.example.org (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).")]
    [InlineData("Paired: calls https://[::1]:5610 (node 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0).")]
    public void AStampedPairingLine_OfTheExactShapeThisCodeWrites_IsShown(string message) =>
        LogOf(JsonSerializer.Serialize(Stamped("pairing", message))).Latest(1).Single().Message.Should().Be(message);

    [Fact]
    public void ThePairingLinesTheCodeWritesToday_AreAllOfTheShapeItChecks()
    {
        var path = Path.Combine(_dir, "pairing.jsonl");
        var log = new BlindPhoneLog(path, new FixedClock());
        var lines = new[]
        {
            $"Blind identity created: {Node}", $"Recovered existing blind identity: {Node}", "Re-pairing started: a new phone code was made.",
            $"Paired: calls https://127.0.0.1:5610 (node {Node}).", "Cleaned up pairing secret committed in previous session.",
        };
        foreach (var line in lines) log.Add(BlindPhoneLog.PairingKind, line);

        log.Latest(10).Select(e => e.Message).Should().Equal(lines.Reverse());
    }

    // ---- the views apply the same rule to a status made by hand ---------------------------------------------------------------------------------

    private static BlindAppStatus StatusWith(params BlindPhoneLog.Entry[] log) =>
        new(Guid.NewGuid(), "device", true, true, null, null, BlindBackupSchedule.Off, false, null, null, [], log, Endpoint: "https://127.0.0.1:5610");

    [Theory]
    [MemberData(nameof(PlainHostileLines))]
    public void TheHomeView_ShowsNoPlainHostileLine_ThatIsNotStamped(string line)
    {
        var status = StatusWith(Unstamped("sync", "Sync failed: " + line), Unstamped("backup", line));

        var view = BlindHomeView.Build(status, null, false);

        FailureCanaryFree(view.Log, view.Problem);
        view.Log.Should().Contain(BlindFailureText.StoredTextHidden);
        view.Problem.Should().Contain(BlindFailureText.StoredTextHidden, "the newest contact problem is the old sync line, shown as the fixed sentence");
        BlindHomeView.ProblemText(status).Should().Be(view.Problem);
    }

    [Theory]
    [MemberData(nameof(ForgedPairingMessages))]
    public void TheHomeView_ShowsNoForgedPairingLine(string message)
    {
        var view = BlindHomeView.Build(StatusWith(Stamped("pairing", message)), null, false);

        FailureCanaryFree(view.Log);
        view.Log.Should().Contain(BlindFailureText.StoredTextHidden);
    }

    [Fact]
    public void TheHomeView_ShowsALineOfThisVersion_AndTheExactPairingLine()
    {
        var pairing = $"Paired: calls https://127.0.0.1:5610 (node {Node}).";
        var view = BlindHomeView.Build(StatusWith(Stamped("pairing", pairing), Stamped("sync", "Sync failed: no answer: connection refused")), null, false);

        view.Log.Should().Contain(pairing).And.Contain("Sync failed: no answer: connection refused").And.NotContain(BlindFailureText.StoredTextHidden);
    }

    [Fact]
    public void TheEntryFilter_IsIdempotent()
    {
        foreach (var entry in new[]
                 {
                     Unstamped("sync", "token=hunter2"), Stamped("sync", "plain"), Stamped("sync", @"C:\x"), Stamped("pairing", "token=hunter2"),
                     Stamped("pairing", $"Paired: calls https://127.0.0.1:5610 (node {Node})."), Unstamped("pairing", $"Paired: calls https://127.0.0.1:5610 (node {Node})."),
                 })
        {
            var once = EntryFilter(entry);
            EntryFilter(once).Should().Be(once);
        }
    }

    private static BlindPhoneLog.Entry EntryFilter(BlindPhoneLog.Entry entry) => entry.ForDisplay();

    // ---- the cards and the job wait: built from a status that holds a hostile log ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(PlainHostileLines))]
    public void AnAccountCard_AndAJobWait_BuiltFromAStatusWhoseLogIsHostile_HoldNoWordOfIt(string line)
    {
        var path = Path.Combine(_dir, "card.jsonl");
        File.WriteAllLines(path, [Legacy("sync", "Sync failed: " + line), Legacy("load", "First load failed: " + line), Legacy("pairing", line)]);
        var log = new BlindPhoneLog(path, new FixedClock()).Latest(30);
        var status = StatusWith([.. log]);
        var entry = new BlindAccountEntry("0123456789abcdef0123456789abcdef", BlindAccountLayouts.Dir, "Friend", BlindAccountPhases.Active, SourceOrigin: "https://127.0.0.1:5610");
        var now = At.AddHours(2);

        var card = BlindAccountCard.Build(new BlindAccountSnapshot(entry, status), TimeZoneInfo.Utc, now);
        var outcome = BlindJobWait.Judge(BlindJobWait.Ask(BlindJobWait.Sync, status, At), status, running: false, now);

        FailureCanaries.Strings(status).Where(Leaks).Should().BeEmpty("the status a head binds");
        FailureCanaries.Strings(card).Where(Leaks).Should().BeEmpty("the card");
        FailureCanaries.Strings(outcome).Where(Leaks).Should().BeEmpty("the job's outcome");
        FailureCanaries.Strings(BlindHomeView.Build(status, null, false)).Where(Leaks).Should().BeEmpty("the home view");
    }

    private static void FailureCanaryFree(params string?[] texts) =>
        texts.Where(Leaks).Should().BeEmpty("no word of a line that this version did not write may reach a screen");
}
