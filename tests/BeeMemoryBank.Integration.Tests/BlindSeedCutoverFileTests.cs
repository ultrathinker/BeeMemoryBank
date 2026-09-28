using BeeMemoryBank.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-stage1 round 3 #3: the cutover's file moves under injected faults. The switch runs under a
/// retry wrapper that repeats the whole action after an IOException, and rollback and finish are
/// repeated by the next start, so a failure between the main database file and one of its sidecars
/// must never cost a byte of the old database: nothing of it is deleted, and every step resumes.
/// A directory where a move wants to put a file is the injected fault (the rename fails there).
///
/// <para>Codex security #3 is the same rule against the other way of losing that file: the durable
/// marker itself, torn or unreadable, read as "no cutover was ever here" — and the directory holding
/// the old database was deleted. A marker nobody can read is an unresolved cutover, never an absent
/// one.</para>
/// </summary>
public sealed class BlindSeedCutoverFileTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bmb-cutover-" + Guid.NewGuid().ToString("N"));

    private string Live => Path.Combine(_data, "beememorybank.db");
    private string InCutover(string name) => Path.Combine(BlindSeedCutover.DirOf(_data), name);

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public async Task ASwitchThatFailsAfterTheMainFileMoved_KeepsTheWal_AndResumes()
    {
        var cutover = StartSwitch();
        Directory.CreateDirectory(InCutover("old.db-wal"));

        await RetriedAsync(cutover.SwitchFiles);

        OldDatabaseFiles().Should().Equal(["old main", "old shm", "old wal"], "no file of the old database is deleted");
        Directory.Delete(InCutover("old.db-wal"));
        await RetriedAsync(cutover.SwitchFiles);
        File.ReadAllText(Live).Should().Be("new main");
        File.ReadAllText(InCutover("old.db-wal")).Should().Be("old wal");
        File.ReadAllText(InCutover("old.db-shm")).Should().Be("old shm");
        cutover.RollBack();
        LiveFiles().Should().Equal("old main", "old wal", "old shm");
    }

    [Fact]
    public async Task ASwitchThatFailsOnTheShm_KeepsIt_AndResumes()
    {
        var cutover = StartSwitch();
        Directory.CreateDirectory(InCutover("old.db-shm"));

        await RetriedAsync(cutover.SwitchFiles);

        OldDatabaseFiles().Should().Equal(["old main", "old shm", "old wal"], "no file of the old database is deleted");
        Directory.Delete(InCutover("old.db-shm"));
        await RetriedAsync(cutover.SwitchFiles);
        File.ReadAllText(Live).Should().Be("new main");
        cutover.RollBack();
        LiveFiles().Should().Equal("old main", "old wal", "old shm");
    }

    [Fact]
    public async Task ARollbackThatFailsAfterTheMainFileCameBack_KeepsTheSidecars_AndResumes()
    {
        var cutover = StartSwitch();
        await RetriedAsync(cutover.SwitchFiles);
        File.ReadAllText(Live).Should().Be("new main", "precondition: switched");
        Directory.CreateDirectory(Live + "-wal");

        await RetriedAsync(cutover.RollBack);

        OldDatabaseFiles().Should().Equal(["old main", "old shm", "old wal"], "no file of the old database is deleted");
        Directory.Delete(Live + "-wal");
        await RetriedAsync(cutover.RollBack);
        LiveFiles().Should().Equal("old main", "old wal", "old shm");
        Directory.Exists(BlindSeedCutover.DirOf(_data)).Should().BeFalse();
    }

    [Fact]
    public async Task AFinishThatFailsAfterTheMainFileMoved_KeepsTheSidecars_AndResumes()
    {
        var cutover = StartSwitch();
        await RetriedAsync(cutover.SwitchFiles);
        cutover.MarkDone(Guid.NewGuid());
        var preSeed = Live + ".pre-seed";
        Directory.CreateDirectory(preSeed + "-wal");

        // Finish is repeated by the next start (the marker says "done"), not by the retry wrapper.
        for (var attempt = 0; attempt < 2; attempt++)
            try { cutover.FinishDone(); }
            // Linux reports a rename onto a directory as an IOException, Windows as access denied.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        OldDatabaseFiles().Should().Equal(["old main", "old shm", "old wal"], "no file of the old database is deleted");
        Directory.Delete(preSeed + "-wal");
        cutover.FinishDone();
        new[] { preSeed, preSeed + "-wal", preSeed + "-shm" }.Select(File.ReadAllText)
            .Should().Equal("old main", "old wal", "old shm");
        Directory.Exists(BlindSeedCutover.DirOf(_data)).Should().BeFalse();
    }

    /// <summary>
    /// Codex security #3: a marker that cannot be read is not "no cutover at all". The old database
    /// beside it is the only way back, and a start that reads the marker as absent drops the whole
    /// directory — old database, old media and all. Here the switch has already moved both aside and
    /// the process died before the new ones were in: the old ones must come back, not vanish.
    /// </summary>
    [Fact]
    public void ATornMarker_WithTheSwitchHalfDone_PutsTheOldDatabaseAndMediaBack()
    {
        Directory.CreateDirectory(_data);
        var cutover = new BlindSeedCutover(_data);
        cutover.Prepare();
        File.WriteAllText(cutover.StagedDbPath, "new main");
        TornMarker();
        // What SwitchFiles did before the process died: both old ones aside, neither new one in.
        File.WriteAllText(InCutover("old.db"), "old main");
        Directory.CreateDirectory(InCutover("old-media"));
        File.WriteAllText(Path.Combine(InCutover("old-media"), "a.enc"), "old media");

        BlindSeedCutover.Recover(_data, NullLogger.Instance);

        File.ReadAllText(Live).Should().Be("old main", "the node must start on a valid database");
        File.ReadAllText(Path.Combine(_data, "media", "a.enc")).Should().Be("old media");
        File.ReadAllText(cutover.StagedDbPath).Should().Be("new main", "nothing is deleted while it is unresolved");
    }

    /// <summary>
    /// The other side of the same rule: with a live database already in place, nothing here can say
    /// whether the switch finished, so nothing is discarded and no new seed is accepted until an
    /// operator resolves it. The old files stay exactly where they are.
    /// </summary>
    [Fact]
    public void ATornMarker_WithACompletedLookingSwitch_KeepsEverything_AndRefusesTheNextSeed()
    {
        Directory.CreateDirectory(_data);
        var cutover = new BlindSeedCutover(_data);
        cutover.Prepare();
        File.WriteAllText(Live, "new main");
        File.WriteAllText(InCutover("old.db"), "old main");
        Directory.CreateDirectory(Path.Combine(_data, "media"));
        File.WriteAllText(Path.Combine(_data, "media", "a.enc"), "new media");
        Directory.CreateDirectory(InCutover("old-media"));
        File.WriteAllText(Path.Combine(InCutover("old-media"), "a.enc"), "old media");
        TornMarker();

        BlindSeedCutover.Recover(_data, NullLogger.Instance);

        File.ReadAllText(Live).Should().Be("new main");
        File.ReadAllText(InCutover("old.db")).Should().Be("old main", "the old database is the only way back");
        File.ReadAllText(Path.Combine(InCutover("old-media"), "a.enc")).Should().Be("old media");

        var refused = () => cutover.Prepare();
        refused.Should().Throw<BlindSeedRejectedException>(
            "a seed must not be started over a cutover nobody can interpret");
        File.ReadAllText(InCutover("old.db")).Should().Be("old main");
    }

    /// <summary>
    /// Codex round 2, security #9, the naming half. The marker's temporary file used to be the fixed
    /// name <c>marker.json.tmp</c>, opened with <c>FileMode.Create</c>: anything already sitting at
    /// that predictable name is truncated and then renamed onto the marker — an unrelated file
    /// consumed, and, when that name is a link someone planted, the write landing wherever it points.
    /// A random name opened with <c>CreateNew</c> cannot be squatted.
    /// </summary>
    [Fact]
    public void WhateverSitsAtTheOldTempName_IsNotConsumedByAMarkerWrite()
    {
        Directory.CreateDirectory(_data);
        var cutover = new BlindSeedCutover(_data);
        cutover.Prepare();
        File.WriteAllText(InCutover("marker.json.tmp"), "someone else's file");

        cutover.WriteMarker(Guid.NewGuid(), BlindSeedCutover.Staged);

        File.ReadAllText(InCutover("marker.json.tmp")).Should().Be("someone else's file",
            "a fixed temporary name is a name anyone can put a file (or a link) at");
        File.ReadAllText(InCutover("marker.json")).Should().Contain(BlindSeedCutover.Staged,
            "the marker itself is still written");
    }

    /// <summary>
    /// The same rule on the paths the cutover reads, writes and deletes: a link is not the cutover's
    /// own file, and working through it reaches outside the data root. A linked marker is refused
    /// outright rather than written over or believed.
    /// </summary>
    [Fact]
    public void ALinkedMarker_IsRefused_AndWhatItPointsAtIsUntouched()
    {
        Directory.CreateDirectory(_data);
        var outside = Path.Combine(Path.GetTempPath(), "bmb-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var target = Path.Combine(outside, "not-ours.json");
            File.WriteAllText(target, "{\"SeedId\":\"6f1c2b34-0f8a-4d31-9a5e-2b7c8d9e0f11\",\"Phase\":\"switching\"}");
            var cutover = new BlindSeedCutover(_data);
            cutover.Prepare();
            if (!TryCreateDirectoryLink(InCutover("marker.json"), outside)) return; // no link, no test

            var write = () => cutover.WriteMarker(Guid.NewGuid(), BlindSeedCutover.Staged);
            write.Should().Throw<BlindSeedRejectedException>("a marker that is a link is not this cutover's marker");
            File.ReadAllText(target).Should().Contain("switching", "nothing outside the data root was written or read as ours");
        }
        finally
        {
            TryDeleteTree(outside);
        }
    }

    /// <summary>
    /// And the directory itself: a <c>blind-cutover</c> that is a link would have its recursive delete
    /// (the drop of a staged cutover, the tidying after a done one) walk into the tree it points at.
    /// Refused instead, with the linked tree intact.
    /// </summary>
    [Fact]
    public void ACutoverDirectoryThatIsALink_IsRefused_AndTheTreeItPointsAtSurvives()
    {
        Directory.CreateDirectory(_data);
        var outside = Path.Combine(Path.GetTempPath(), "bmb-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "not the cutover's to delete");
            var cutover = new BlindSeedCutover(_data);
            if (!TryCreateDirectoryLink(BlindSeedCutover.DirOf(_data), outside)) return; // no link, no test

            var prepare = () => cutover.Prepare();
            prepare.Should().Throw<BlindSeedRejectedException>("the cutover directory must be a plain directory of ours");
            File.Exists(Path.Combine(outside, "sentinel.txt")).Should().BeTrue(
                "a recursive delete of a linked directory takes the tree it points at with it");
        }
        finally
        {
            TryDeleteTree(outside);
        }
    }

    /// <summary>
    /// A directory link, created the way each platform allows without elevation: a junction on
    /// Windows (no privilege needed), a symbolic link elsewhere. False when the platform refused —
    /// the test that asked then has nothing to exercise and says so by returning.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                cmd.WaitForExit();
                return cmd.ExitCode == 0 && Directory.Exists(link);
            }
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The test's own temporary tree, links in it or not.</summary>
    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // A leftover temporary directory is the test's own; nothing depends on it going.
        }
    }

    /// <summary>A marker file that was cut off mid-write — readable as a file, not as a marker.</summary>
    private void TornMarker() =>
        File.WriteAllText(InCutover("marker.json"), "{\"SeedId\":\"6f1c2b34-0f8a-4d31-9a5e-2b7c8d9e0f11\",\"Phase\":\"swit");

    /// <summary>A live database with both sidecars, a staged new one, and the marker at "switching".</summary>
    private BlindSeedCutover StartSwitch()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Live, "old main");
        File.WriteAllText(Live + "-wal", "old wal");
        File.WriteAllText(Live + "-shm", "old shm");
        var cutover = new BlindSeedCutover(_data);
        cutover.Prepare();
        File.WriteAllText(cutover.StagedDbPath, "new main");
        cutover.WriteMarker(Guid.NewGuid(), BlindSeedCutover.Switching);
        return cutover;
    }

    /// <summary>The way the seed service runs a switch or rollback: repeated after an IOException.</summary>
    private static async Task RetriedAsync(Action action)
    {
        try
        {
            await SnapshotService.SwapDbFileWithRetryAsync(action, () => { }, maxAttempts: 3, delay: _ => Task.CompletedTask);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The injected fault outlasted the retries, as a real one can.
        }
    }

    /// <summary>Every file of the old database, wherever the cutover has put it.</summary>
    private string[] OldDatabaseFiles() => Directory.GetFiles(_data, "*", SearchOption.AllDirectories)
        .Select(File.ReadAllText).Where(content => content.StartsWith("old ", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToArray();

    private string[] LiveFiles() => new[] { Live, Live + "-wal", Live + "-shm" }.Select(File.ReadAllText).ToArray();
}
