using BeeMemoryBank.Core.IO;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.Core.Tests;

public class OwnerOnlyFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bmb_owneronly_{Guid.NewGuid():N}");

    public OwnerOnlyFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void WriteNew_LeavesTheBytesOnlyUnderTheFinalName()
    {
        var path = Path.Combine(_dir, "secret.bin");

        OwnerOnlyFile.WriteNew(path, [1, 2, 3]);

        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
        Directory.GetFiles(_dir).Should().ContainSingle();
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void WriteNew_NeverReplacesAnExistingFile_AndLeavesNoTemp()
    {
        var path = Path.Combine(_dir, "secret.bin");
        File.WriteAllBytes(path, [9]);

        var act = () => OwnerOnlyFile.WriteNew(path, [1, 2, 3]);

        act.Should().Throw<IOException>();
        File.ReadAllBytes(path).Should().Equal(9);
        Directory.GetFiles(_dir).Should().ContainSingle();
    }

    [Fact]
    public void WriteNew_ThatFailsBeforeTheRename_LeavesNothing()
    {
        var path = Path.Combine(_dir, "secret.bin");

        var act = () => OwnerOnlyFile.WriteNew(path, [1, 2, 3], _ => throw new IOException("simulated crash"));

        act.Should().Throw<IOException>().WithMessage("simulated crash");
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    // ─── Windows ACLs (review release-a #7) ─────────────────────────────────

    [WindowsAclFact]
    public void CreateNew_InAFolderEveryAccountCanRead_IsOwnerOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_dir);
        var path = Path.Combine(_dir, "secret.bin");

        using (var file = OwnerOnlyFile.CreateNew(path)) file.WriteByte(1);

        WindowsAcl.UsersCanRead(path).Should().BeFalse("the folder's inherited BUILTIN\\Users entry is not taken over");
        WindowsAcl.IsProtected(path).Should().BeTrue("nothing the folder gets later is inherited either");
        WindowsAcl.OwnerHasFullControl(path).Should().BeTrue();
        OwnerOnlyFile.IsOwnerOnly(path).Should().BeTrue();
    }

    [WindowsAclFact]
    public void CreateNew_OnAVolumeWithoutAcls_StillCreatesTheFile_InsteadOfFailingEverySnapshotThere()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_dir);
        var path = Path.Combine(_dir, "secret.bin");

        // The seam says the volume keeps no ACLs (FAT32/exFAT): setting one there throws, so the step is not attempted.
        using (var file = OwnerOnlyFile.CreateNew(path, volumeKeepsAcls: _ => false)) file.WriteByte(1);

        File.ReadAllBytes(path).Should().Equal(1);
        WindowsAcl.UsersCanRead(path).Should().BeTrue("there is nothing to tighten on such a volume, and the file is not refused for it");
    }

    [WindowsAclFact]
    public void WriteNew_TheFinalFileKeepsTheOwnerOnlyAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_dir);
        var path = Path.Combine(_dir, "secret.bin");

        OwnerOnlyFile.WriteNew(path, [1, 2, 3]);

        WindowsAcl.UsersCanRead(path).Should().BeFalse("the rename keeps the temp file's ACL");
        WindowsAcl.OwnerHasFullControl(path).Should().BeTrue();
    }

    [WindowsAclFact]
    public void TryTighten_RepairsAnInheritedAclInPlace_AndLeavesAnOwnerOnlyFileAlone()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAcl.MakeReadableByAllUsers(_dir);
        var path = Path.Combine(_dir, "secret.bin");
        File.WriteAllBytes(path, [7, 8, 9]); // as an older build wrote it
        WindowsAcl.UsersCanRead(path).Should().BeTrue("precondition: every account can read it");

        OwnerOnlyFile.TryTighten(path, out var problem).Should().BeTrue();

        problem.Should().BeNull();
        WindowsAcl.UsersCanRead(path).Should().BeFalse();
        WindowsAcl.OwnerHasFullControl(path).Should().BeTrue("the owner keeps its access");
        File.ReadAllBytes(path).Should().Equal(7, 8, 9);
        OwnerOnlyFile.TryTighten(path, out _).Should().BeFalse("a file that is already owner-only is not rewritten");
    }

    [Fact]
    public void TryTighten_AMissingFile_IsNothingToDo()
    {
        OwnerOnlyFile.TryTighten(Path.Combine(_dir, "missing.bin"), out var problem).Should().BeFalse();
        problem.Should().BeNull();
    }
}
