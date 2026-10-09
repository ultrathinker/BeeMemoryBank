using BeeMemoryBank.AppPaths;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>At most one node process works on a data folder (week review, F4).</summary>
public sealed class InstanceGuardTests
{
    // Left to the OS temp cleaner: a test deletes nothing.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-instance-" + Guid.NewGuid().ToString("N"));

    private string Folder(string name) => Path.Combine(_root, name);

    [Fact]
    public void ASecondStartOnTheSameFolder_IsRefused()
    {
        using var first = InstanceGuard.Acquire(Folder("data"));

        var second = () => InstanceGuard.Acquire(Folder("data"), TimeSpan.FromMilliseconds(200));

        second.Should().Throw<InstanceInUseException>().Which.Message.Should().Contain("data");
    }

    [Fact]
    public void AfterTheFirstHolderEnds_TheFolderCanBeTakenAgain()
    {
        InstanceGuard.Acquire(Folder("again")).Dispose();

        using var next = InstanceGuard.Acquire(Folder("again"), TimeSpan.FromMilliseconds(200));

        next.Should().NotBeNull();
    }

    [Fact]
    public async Task ARestartWhoseOldProcessIsStillShuttingDown_WaitsForIt()
    {
        var old = InstanceGuard.Acquire(Folder("restart"));
        var release = Task.Run(async () => { await Task.Delay(300); old.Dispose(); });

        using var next = InstanceGuard.Acquire(Folder("restart"), TimeSpan.FromSeconds(10));

        await release;
        next.Should().NotBeNull();
    }

    [Fact]
    public void TwoFoldersDoNotShareALock()
    {
        using var a = InstanceGuard.Acquire(Folder("a"));
        using var b = InstanceGuard.Acquire(Folder("b"), TimeSpan.FromMilliseconds(200));

        b.Should().NotBeNull();
    }

    [Fact]
    public void TheVaultLeaseStaysSharedByDesign_WhileTheInstanceLockIsExclusive()
    {
        var data = Folder("lease");
        Directory.CreateDirectory(data);
        using var lease1 = VaultStartup.TryAcquireShared(data);
        using var lease2 = VaultStartup.TryAcquireShared(data);
        lease1.Should().NotBeNull();
        lease2.Should().NotBeNull("a CLI command next to a node is allowed");

        using var instance = InstanceGuard.Acquire(data);

        var second = () => InstanceGuard.Acquire(data, TimeSpan.Zero);
        second.Should().Throw<InstanceInUseException>();
    }

    // Windows only: there the lock file stays readable; on Linux and macOS .NET's own File.Copy honours the (advisory) flock, cp, tar and restic do not.
    [WindowsAclFact]
    public void ACopyOfTheLiveFolder_StillWorksOnWindows_WhereTheLockFileIsReadable()
    {
        var data = Folder("copy");
        using var held = InstanceGuard.Acquire(data);

        var copy = () => File.Copy(InstanceGuard.LockPathFor(data), Path.Combine(_root, "copy-of-lock"));

        copy.Should().NotThrow("Explorer, robocopy and backup tools must not fail on the data folder of a running node");
    }
}
