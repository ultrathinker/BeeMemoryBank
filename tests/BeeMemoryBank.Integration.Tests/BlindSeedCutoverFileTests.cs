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

    /// <summary>
    /// Once the seed is committed ("done"), the old database is no way back, only a copy of the vault under its old
    /// key: it is wiped (zeros, then deleted), sidecars included, and no <c>.pre-seed</c> copy is kept.
    /// </summary>
    [Fact]
    public async Task AFinishedCutover_WipesTheOldDatabase_AndKeepsNoPreSeedCopy()
    {
        var cutover = StartSwitch();
        await RetriedAsync(cutover.SwitchFiles);
        cutover.MarkDone(Guid.NewGuid());

        cutover.FinishDone();

        File.ReadAllText(Live).Should().Be("new main");
        Directory.Exists(BlindSeedCutover.DirOf(_data)).Should().BeFalse();
        Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories).Select(File.ReadAllText)
            .Should().NotContain(t => t.StartsWith("old"), "no file of the old database is left anywhere");
        File.Exists(Live + ".pre-seed").Should().BeFalse();
    }

    /// <summary>A finish interrupted half way (the next start repeats it: the marker says "done") still ends with nothing old left.</summary>
    [Fact]
    public async Task AFinishInterruptedHalfway_IsCompletedByTheNextStart()
    {
        var cutover = StartSwitch();
        await RetriedAsync(cutover.SwitchFiles);
        cutover.MarkDone(Guid.NewGuid());
        BlindSeedCutover.WipeFile(InCutover("old.db")); // the main file went; the process died before the sidecars

        BlindSeedCutover.Recover(_data, NullLogger.Instance);

        Directory.Exists(BlindSeedCutover.DirOf(_data)).Should().BeFalse();
        Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories).Select(File.ReadAllText)
            .Should().NotContain(t => t.StartsWith("old"));
    }

    /// <summary>A .pre-seed copy an older build left after a completed seed is wiped at start.</summary>
    [Fact]
    public void APreSeedCopyLeftByAnOlderBuild_IsWipedAtStart()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Live, "live main");
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.WriteAllText(Live + ".pre-seed" + suffix, "old copy" + suffix);

        BlindSeedCutover.Recover(_data, NullLogger.Instance);

        Directory.EnumerateFiles(_data).Select(Path.GetFileName).Should().Equal("beememorybank.db");
        File.ReadAllText(Live).Should().Be("live main");
    }

    /// <summary>DK2's rule holds: while a cutover is unresolved, nothing is deleted, an older pre-seed copy included.</summary>
    [Fact]
    public void AnUnresolvedCutover_DeletesNothing_NotEvenAnOlderPreSeedCopy()
    {
        StartSwitch();
        File.Move(Live, InCutover("old.db"));
        File.WriteAllText(Live, "new main");
        File.WriteAllText(InCutover("marker.json"), "{ torn");
        File.WriteAllText(Live + ".pre-seed", "old copy");

        BlindSeedCutover.Recover(_data, NullLogger.Instance);

        File.Exists(InCutover("old.db")).Should().BeTrue("unresolved: the old database stays");
        File.ReadAllText(Live + ".pre-seed").Should().Be("old copy", "nothing is deleted while the cutover is unresolved");
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
