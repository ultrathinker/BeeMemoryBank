using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-stage1 round 3 #3: the cutover's file moves under injected faults. The switch runs under a
/// retry wrapper that repeats the whole action after an IOException, and rollback and finish are
/// repeated by the next start, so a failure between the main database file and one of its sidecars
/// must never cost a byte of the old database: nothing of it is deleted, and every step resumes.
/// A directory where a move wants to put a file is the injected fault (the rename fails there).
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
