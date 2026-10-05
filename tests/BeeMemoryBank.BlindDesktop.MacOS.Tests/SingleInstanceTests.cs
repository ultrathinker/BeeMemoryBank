using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>
/// "Only one copy runs" on Unix: a lock file plus a Unix domain socket in the app's private folder. The logic is plain .NET (FileStream with
/// FileShare.None, a socket on a path), so it runs and is proved on every operating system the tests run on; what is macOS-specific (file
/// modes of the folder and socket) is marked. The real second PROCESS is proved by the published app's <c>--self-check-second-start</c>.
/// </summary>
public sealed class SingleInstanceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void TheFirstCopyGetsTheLock_TheSecondIsRefused_AndAfterTheFirstEndsTheLockIsFree()
    {
        using var folder = new TempFolder();

        var first = FileLockSingleInstance.TryAcquire(folder.Path);
        first.Should().NotBeNull();
        FileLockSingleInstance.TryAcquire(folder.Path).Should().BeNull("the lock is taken");
        FileLockSingleInstance.TryAcquire(folder.Path).Should().BeNull("and it stays refused");

        first!.Dispose();

        using var again = FileLockSingleInstance.TryAcquire(folder.Path);
        again.Should().NotBeNull("the lock went with the first copy");
    }

    [Fact]
    public void TwoDifferentFolders_AreTwoDifferentApps()
    {
        using var a = new TempFolder();
        using var b = new TempFolder();

        using var first = FileLockSingleInstance.TryAcquire(a.Path);
        using var second = FileLockSingleInstance.TryAcquire(b.Path);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
    }

    [Fact]
    public void ASecondStart_AsksTheRunningCopyToShowItsWindow()
    {
        using var folder = new TempFolder();
        using var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        using var shown = new ManualResetEventSlim();
        instance.Listen(shown.Set);

        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();

        shown.Wait(Wait).Should().BeTrue("the running copy was told to show its window");
    }

    [Fact]
    public void EverySecondStart_IsDelivered()
    {
        using var folder = new TempFolder();
        using var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        using var three = new CountdownEvent(3);
        instance.Listen(() => three.Signal());

        for (var i = 0; i < 3; i++) FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();

        three.Wait(Wait).Should().BeTrue("three second starts, three activations");
    }

    [Fact]
    public void ARequestThatArrivesBeforeTheWindowListens_IsDeliveredWhenItDoes()
    {
        using var folder = new TempFolder();
        using var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();
        Thread.Sleep(300); // let the accept loop receive it while nobody listens yet

        using var shown = new ManualResetEventSlim();
        instance.Listen(shown.Set);

        shown.Wait(Wait).Should().BeTrue("the start-up of the window is not a reason to lose the request");
    }

    [Fact]
    public void WhenNoCopyRuns_TheSignalIsNotAccepted()
    {
        using var folder = new TempFolder();

        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeFalse();
    }

    [Fact]
    public void AfterTheCopyEnds_ItsSocketIsGone_AndTheSignalIsNotAccepted()
    {
        using var folder = new TempFolder();
        var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        var socket = FileLockSingleInstance.SocketPathFor(folder.Path)!;
        File.Exists(socket).Should().BeTrue("a running copy listens on its socket file");

        instance.Dispose();

        File.Exists(socket).Should().BeFalse("the socket file is the copy's own and goes with it");
        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeFalse();
        File.Exists(Path.Combine(folder.Path, FileLockSingleInstance.LockFileName)).Should().BeTrue("the lock file stays: two processes must always lock the same file");
    }

    [Fact]
    public void ASocketFileLeftByACrash_IsReplacedByTheNextStart()
    {
        using var folder = new TempFolder();
        var socket = FileLockSingleInstance.SocketPathFor(folder.Path)!;
        File.WriteAllText(socket, "left by a process that died");

        using var instance = FileLockSingleInstance.TryAcquire(folder.Path);
        using var shown = new ManualResetEventSlim();
        instance!.Listen(shown.Set);

        instance.ActivationUnavailable.Should().BeNull();
        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();
        shown.Wait(Wait).Should().BeTrue();
    }

    [Fact]
    public void AHandlerThatThrows_IsReported_AndTheNextStartStillWorks()
    {
        using var folder = new TempFolder();
        using var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        var errors = new List<Exception>();
        using var second = new ManualResetEventSlim();
        var calls = 0;
        instance.Listen(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("the window is not ready");
            second.Set();
        }, ex => { lock (errors) errors.Add(ex); });

        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();
        SpinWait.SpinUntil(() => { lock (errors) return errors.Count == 1; }, Wait).Should().BeTrue("the failure is reported, not swallowed");
        FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();

        second.Wait(Wait).Should().BeTrue("one failed handler does not end the listener");
        errors.Should().ContainSingle().Which.Message.Should().Be("the window is not ready");
    }

    [Fact]
    public void AStrayConnection_ShowsNothing_AndAnIdleOneDoesNotBlockTheRealSignal()
    {
        using var folder = new TempFolder();
        using var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        var shown = 0;
        using var real = new ManualResetEventSlim();
        instance.Listen(() =>
        {
            Interlocked.Increment(ref shown);
            real.Set();
        });
        var path = FileLockSingleInstance.SocketPathFor(folder.Path)!;

        using (var garbage = Connect(path))
            garbage.Send(Encoding.ASCII.GetBytes("open sesame\n"));
        using (var idle = Connect(path))
        {
            // connected and silent: must not hold up the next client
            FileLockSingleInstance.SignalRunningInstance(folder.Path).Should().BeTrue();
            real.Wait(Wait).Should().BeTrue("a silent connection is not in the way");
            idle.Connected.Should().BeTrue();
        }

        Thread.Sleep(300);
        shown.Should().Be(1, "only the real request showed the window");
    }

    [Fact]
    public void ALongFolderPath_GetsAShortSocketPath_AndStillWorks()
    {
        using var folder = new TempFolder();
        var deep = Path.Combine(folder.Path, new string('a', 40), new string('b', 40), "BeeMemoryBankBlind");
        FileLockSingleInstance.SocketPathFor(deep).Should().NotBeNull();
        Encoding.UTF8.GetByteCount(FileLockSingleInstance.SocketPathFor(deep)!).Should().BeLessThanOrEqualTo(100, "sockaddr_un.sun_path holds about 104 bytes");
        FileLockSingleInstance.SocketPathFor(deep).Should().NotStartWith(deep, "it moved out of the long folder");

        using var instance = FileLockSingleInstance.TryAcquire(deep)!;
        using var shown = new ManualResetEventSlim();
        instance.Listen(shown.Set);

        instance.ActivationUnavailable.Should().BeNull();
        FileLockSingleInstance.SignalRunningInstance(deep).Should().BeTrue();
        shown.Wait(Wait).Should().BeTrue();
    }

    [Fact]
    public void AShortFolderPath_KeepsTheSocketInsideTheFolder()
    {
        using var folder = new TempFolder();

        var socket = FileLockSingleInstance.SocketPathFor(folder.Path);

        if (Encoding.UTF8.GetByteCount(Path.Combine(folder.Path, FileLockSingleInstance.SocketFileName)) <= 100)
            socket.Should().Be(Path.Combine(folder.Path, FileLockSingleInstance.SocketFileName));
    }

    [Fact]
    public void ABlankFolder_IsRefused()
    {
        var act = () => FileLockSingleInstance.TryAcquire(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void APlainIoFailure_IsNotMistakenForAnotherCopy()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, FileLockSingleInstance.LockFileName)); // the lock "file" is a folder: a real problem

        var act = () => FileLockSingleInstance.TryAcquire(folder.Path);

        act.Should().Throw<Exception>("a failure that is not 'somebody else holds it' must surface, not look like a second copy");
    }

    [Fact]
    public void OnlyASharingViolation_CountsAsTheLockBeingTaken()
    {
        FileLockSingleInstance.IsSharingViolation(new IOException("The process cannot access the file '/x' because it is being used by another process.")).Should().BeTrue();
        FileLockSingleInstance.IsSharingViolation(new IOException("sharing violation") { HResult = unchecked((int)0x80070020) }).Should().BeTrue();
        FileLockSingleInstance.IsSharingViolation(new IOException("Disk full") { HResult = unchecked((int)0x80070070) }).Should().BeFalse();
        FileLockSingleInstance.IsSharingViolation(new IOException("Permission denied")).Should().BeFalse();
    }

    [MacOnlyFact]
    [SupportedOSPlatform("macos")]
    public void TheLockFileAndTheSocket_AreForTheUserOnly()
    {
        using var folder = new TempFolder();
        using var instance = FileLockSingleInstance.TryAcquire(folder.Path)!;
        var user = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        File.GetUnixFileMode(Path.Combine(folder.Path, FileLockSingleInstance.LockFileName)).Should().Be(user);
        File.GetUnixFileMode(FileLockSingleInstance.SocketPathFor(folder.Path)!).Should().Be(user, "only the same user can talk to the running copy");
    }

    private static Socket Connect(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        return socket;
    }
}
