using BeeMemoryBank.Core.IO;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The working copies of a join and a restore (review revsrv F10, revios F5): made in the data folder, readable by the owner only from the
/// first byte, and swept at the next start if a kill left them.
/// </summary>
public class SnapshotStagingTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"bmb_staging_{Guid.NewGuid():N}");

    public SnapshotStagingTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NewDirectory_IsInTheDataFolder_AndNoOtherAccountOfAUnixMachineCanEnterIt()
    {
        var dir = SnapshotStaging.NewDirectory(_data);

        Path.GetDirectoryName(dir).Should().Be(SnapshotStaging.DirIn(_data));
        Directory.Exists(dir).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(dir).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void NewArchive_IsAnEmptyOwnerOnlyFile_ThatKeepsItsPermissionsWhenWrittenThroughFileCreate()
    {
        var archive = SnapshotStaging.NewArchive(_data);

        Path.GetDirectoryName(archive).Should().Be(SnapshotStaging.DirIn(_data));
        new FileInfo(archive).Length.Should().Be(0);
        // The way the join writes it.
        using (var fs = File.Create(archive)) fs.Write([1, 2, 3]);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(archive).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        OwnerOnlyFile.IsOwnerOnly(archive).Should().BeTrue();
    }

    [WindowsAclFact]
    public void OnWindows_WhatIsExtractedIntoTheFolder_IsOwnerOnly_EvenInADataFolderEveryAccountCanRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_data);

        var dir = SnapshotStaging.NewDirectory(_data);
        var extracted = Path.Combine(dir, "beememorybank.db");
        File.WriteAllBytes(extracted, [1]); // what ExtractToFile makes: a new file, with whatever the folder passes on

        WindowsAcl.UsersCanRead(extracted).Should().BeFalse("the extracted database is the vault in clear");
        WindowsAcl.OwnerHasFullControl(extracted).Should().BeTrue();
    }

    [Fact]
    public void CreateDirectory_NeverAdoptsAFolderThatIsAlreadyThere()
    {
        var path = Path.Combine(_data, "taken");
        Directory.CreateDirectory(path);

        var act = () => OwnerOnlyFile.CreateDirectory(path);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Sweep_RemovesWhatAnInterruptedJoinOrRestoreLeft_FilesAndFolders_AndNothingElse()
    {
        var archive = SnapshotStaging.NewArchive(_data);
        File.WriteAllBytes(archive, [1, 2, 3]);
        var extracted = SnapshotStaging.NewDirectory(_data);
        Directory.CreateDirectory(Path.Combine(extracted, "media"));
        File.WriteAllText(Path.Combine(extracted, "beememorybank.db"), "the vault, in clear");
        File.WriteAllText(Path.Combine(extracted, "media", "a.enc"), "x");
        var copy = SnapshotStaging.NewFile(_data);
        var other = Path.Combine(SnapshotStaging.DirIn(_data), "not-ours.txt");
        File.WriteAllText(other, "someone else's");

        SnapshotStaging.Sweep(_data).Should().Be(3);

        File.Exists(archive).Should().BeFalse();
        Directory.Exists(extracted).Should().BeFalse();
        File.Exists(copy).Should().BeFalse();
        File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public void TryCleanupDirectory_WhenADeleteFails_RetriesAndDoesNotClaimTheClearFilesWereRemoved()
    {
        var extracted = SnapshotStaging.NewDirectory(_data);
        var database = Path.Combine(extracted, "beememorybank.db");
        File.WriteAllText(database, "clear vault");
        File.WriteAllText(database + "-wal", "clear wal");
        File.WriteAllText(database + "-shm", "shared memory");

        var attempts = 0;
        SnapshotStaging.TryCleanupDirectory(extracted, _ => { attempts++; return false; }).Should().BeFalse();

        attempts.Should().Be(3, "cleanup retries before a caller reports repair-required");
        File.Exists(database).Should().BeTrue();
        File.Exists(database + "-wal").Should().BeTrue();
        File.Exists(database + "-shm").Should().BeTrue();

        SnapshotStaging.TryCleanupDirectory(extracted).Should().BeTrue();
        Directory.Exists(extracted).Should().BeFalse();
    }

    [Fact]
    public void TryCleanupFile_RemovesTheStagingFileAndEachSqliteSidecar()
    {
        var copy = SnapshotStaging.NewFile(_data);
        foreach (var name in Names(copy)) File.WriteAllText(name, "clear pages");

        SnapshotStaging.TryCleanupFile(copy).Should().BeTrue();

        foreach (var name in Names(copy)) File.Exists(name).Should().BeFalse(name);
    }

    [Fact]
    public void TryCleanupFile_WhenASidecarCannotBeRemoved_ReportsFailure_AndDoesNotClaimTheCopyIsGone()
    {
        var copy = SnapshotStaging.NewFile(_data);
        // A folder under a sidecar's name cannot be deleted as a file on any platform, so it stands for a sidecar that stays.
        Directory.CreateDirectory(copy + "-journal");

        SnapshotStaging.TryCleanupFile(copy).Should().BeFalse("a sidecar is still there: the caller must report repair-required");

        Directory.Delete(copy + "-journal");
        SnapshotStaging.TryCleanupFile(copy).Should().BeTrue();
    }

    [Fact]
    public void TryCleanupFile_HandsEachFileOfTheCopyToTheDeleteCallback_SidecarsIncluded()
    {
        var copy = SnapshotStaging.NewFile(_data);
        foreach (var name in Names(copy)) File.WriteAllText(name, "clear pages");
        var handed = new List<string>();

        SnapshotStaging.TryCleanupFile(copy, file => { handed.Add(file); File.Delete(file); return true; }).Should().BeTrue();

        handed.Should().BeEquivalentTo(Names(copy), "a caller that wipes its copy wipes the sidecars too");
    }

    [Fact]
    public void The_journal_and_the_WAL_that_SQLite_creates_beside_a_staging_file_are_owner_only()
    {
        var copy = SnapshotStaging.NewFile(_data);
        using var conn = new SqliteConnection($"Data Source={copy};Pooling=False");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=DELETE; CREATE TABLE t (x); BEGIN; INSERT INTO t VALUES (1);");
        File.Exists(copy + "-journal").Should().BeTrue("the rollback journal of the open transaction");
        AssertOwnerOnly(copy + "-journal");

        Exec(conn, "COMMIT; PRAGMA journal_mode=WAL; INSERT INTO t VALUES (2);");
        File.Exists(copy + "-wal").Should().BeTrue("the write-ahead log of the WAL mode");
        AssertOwnerOnly(copy + "-wal");
        AssertOwnerOnly(copy + "-shm");
    }

    [Fact]
    public void An_existing_staging_folder_that_others_can_read_is_made_owner_only_before_a_copy_is_made_in_it()
    {
        var root = SnapshotStaging.DirIn(_data);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows())
            WindowsAcl.MakeReadableByAllUsers(root);
        else
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var copy = SnapshotStaging.NewFile(_data);

        if (OperatingSystem.IsWindows())
            WindowsAcl.DirectoryUsersCanRead(root).Should().BeFalse("SQLite creates the journal and the WAL of the copy inside this folder");
        else
            (File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)).Should().Be(0);
        AssertOwnerOnly(copy);
    }

    [Fact]
    public void Sweep_RemovesTheSqliteSidecarsOfACopyThatIsGone()
    {
        var copy = SnapshotStaging.NewFile(_data);
        File.Delete(copy);
        File.WriteAllText(copy + "-wal", "clear wal");
        File.WriteAllText(copy + "-shm", "shared memory");

        SnapshotStaging.Sweep(_data);

        File.Exists(copy + "-wal").Should().BeFalse();
        File.Exists(copy + "-shm").Should().BeFalse();
    }

    /// <summary>A staging file and each SQLite sidecar of it.</summary>
    private static string[] Names(string copy) => [copy, copy + "-journal", copy + "-wal", copy + "-shm"];

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsAcl.UsersCanRead(path).Should().BeFalse($"{path} holds the vault in clear");
            WindowsAcl.OwnerHasFullControl(path).Should().BeTrue(path);
        }
        else
        {
            (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite))
                .Should().Be(0, path);
        }
    }
}
