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

    /// <summary>
    /// BMB-188: several starts creating the same secret at the same instant. On Linux and macOS
    /// <c>File.Move(overwrite: false)</c> is a check and then a rename that replaces, so writers that all saw no file
    /// all "won", each later one silently replacing the one before: a start went on with bytes that were no longer on
    /// disk. Exactly one may win, and the file holds what that one wrote.
    /// </summary>
    [Fact]
    public void WriteNew_ManyAtOnce_ExactlyOneWins_AndTheFileHoldsWhatItWrote()
    {
        const int rounds = 200, writers = 4;
        for (var round = 0; round < rounds; round++)
        {
            var path = Path.Combine(_dir, $"race-{round}.bin");
            using var barrier = new Barrier(writers);
            var won = new bool[writers];

            var tasks = Enumerable.Range(0, writers).Select(i => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    OwnerOnlyFile.WriteNew(path, [(byte)i]);
                    won[i] = true;
                }
                catch (IOException)
                {
                    // Lost: the file was already there.
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(30)).Should().BeTrue();

            won.Count(w => w).Should().Be(1, $"round {round}: exactly one writer creates the file");
            File.ReadAllBytes(path).Should().Equal([(byte)Array.IndexOf(won, true)], $"round {round}: the file holds the winner's bytes");
        }

        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("a loser removes its temp file");
    }

    [Fact]
    public void MoveNoReplace_RenamesTheFile_AndKeepsItsOwnerOnlyMode()
    {
        var from = Path.Combine(_dir, "secret.bin.tmp");
        var to = Path.Combine(_dir, "secret.bin");
        using (var file = OwnerOnlyFile.CreateNew(from)) file.Write([1, 2, 3]);

        OwnerOnlyFile.MoveNoReplace(from, to);

        Directory.GetFiles(_dir).Should().Equal(to);
        File.ReadAllBytes(to).Should().Equal(1, 2, 3);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(to).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void MoveNoReplace_OntoAnExistingFile_Throws_AndTouchesNeither()
    {
        var from = Path.Combine(_dir, "new.bin");
        var to = Path.Combine(_dir, "existing.bin");
        File.WriteAllBytes(from, [1]);
        File.WriteAllBytes(to, [9]);

        var act = () => OwnerOnlyFile.MoveNoReplace(from, to);

        act.Should().Throw<IOException>();
        File.ReadAllBytes(to).Should().Equal(9);
        File.ReadAllBytes(from).Should().Equal(1);
    }

    /// <summary>A dangling symlink at the final name is something there: not replaced, and nothing written through it.</summary>
    [Fact]
    public void MoveNoReplace_OntoADanglingSymlink_Throws_AndWritesNothingThroughIt()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need privileges there
        var from = Path.Combine(_dir, "new.bin");
        var to = Path.Combine(_dir, "secret.bin");
        var pointedAt = Path.Combine(_dir, "elsewhere.bin");
        File.WriteAllBytes(from, [1]);
        File.CreateSymbolicLink(to, pointedAt);

        var act = () => OwnerOnlyFile.MoveNoReplace(from, to);

        act.Should().Throw<IOException>();
        File.Exists(pointedAt).Should().BeFalse("the link was not followed");
        new FileInfo(to).LinkTarget.Should().Be(pointedAt, "the link was not replaced");
    }

    /// <summary>Any refusal other than "already there" goes to File.Move, which reports it in its own words.</summary>
    [Fact]
    public void MoveNoReplace_OfAMissingFile_ThrowsFileNotFound()
    {
        var act = () => OwnerOnlyFile.MoveNoReplace(Path.Combine(_dir, "missing.bin"), Path.Combine(_dir, "secret.bin"));

        act.Should().Throw<FileNotFoundException>();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    /// <summary>
    /// BMB-188, without the write and the flush around it: the narrowest form of the race, so a regression shows in the
    /// first rounds. Every mover has its own file; exactly one gets the name, and the losers keep theirs.
    /// </summary>
    [Fact]
    public void MoveNoReplace_ManyAtOnce_ExactlyOneGetsTheName()
    {
        const int rounds = 300, movers = 8;
        for (var round = 0; round < rounds; round++)
        {
            var to = Path.Combine(_dir, $"race-{round}.bin");
            var from = Enumerable.Range(0, movers).Select(i => Path.Combine(_dir, $"race-{round}-{i}.tmp")).ToArray();
            for (var i = 0; i < movers; i++) File.WriteAllBytes(from[i], [(byte)i]);
            using var barrier = new Barrier(movers);
            var won = new bool[movers];

            var tasks = Enumerable.Range(0, movers).Select(i => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    OwnerOnlyFile.MoveNoReplace(from[i], to);
                    won[i] = true;
                }
                catch (IOException)
                {
                    // Lost: the name was taken.
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(tasks, TimeSpan.FromSeconds(30)).Should().BeTrue();

            won.Count(w => w).Should().Be(1, $"round {round}: exactly one mover gets the name");
            var winner = Array.IndexOf(won, true);
            File.ReadAllBytes(to).Should().Equal([(byte)winner], $"round {round}: the name holds the winner's file");
            for (var i = 0; i < movers; i++)
                File.Exists(from[i]).Should().Be(i != winner, $"round {round}: only the winner's file was moved");
        }
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
