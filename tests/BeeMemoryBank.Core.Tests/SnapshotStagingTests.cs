using BeeMemoryBank.Core.IO;
using BeeMemoryBank.TestSupport;

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
}
