using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// <c>--self-check</c>: the headless proof that the published binary starts and its composition is sound. Here it runs in this process
/// over the platform of the machine the tests run on (the real Windows or macOS adapters, in a scratch folder, with the key store replaced
/// by a trap), and over stand-in platforms that misbehave, so that every red line of the check is shown to go red.
/// </summary>
public sealed class HeadlessSelfCheckTests
{
    private static (int Exit, string Output) Run(IBlindDesktopPlatform platform, StartupOptions options)
    {
        var output = new StringWriter();
        var exit = HeadlessSelfCheck.Run(options, platform, output);
        return (exit, output.ToString());
    }

    private static StartupOptions Check(string folder) => new(false, folder, SelfCheck: true);

    [Fact]
    public void TheRealPlatform_PassesTheCheck_InAScratchFolder_AndNoKeyIsMadeOrRead()
    {
        var folder = TestFolders.New("selfcheck");

        var (exit, output) = Run(PlatformSelector.Create(folder), Check(folder));

        exit.Should().Be(0, output);
        output.Should().Contain("SELF-CHECK PASSED").And.NotContain("FAIL");
        output.Should().Contain("OK   composition").And.Contain("OK   state store")
            .And.Contain("OK   autostart").And.Contain("OK   one copy").And.Contain("OK   no key-store call");
        // No key blob, no database: nothing of the identity was made.
        Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)!)
            .Should().NotContain(n => n.Contains("seed") || n.Contains("backup-key") || n.Contains("pairing-secret") || n.EndsWith(".db") || n == "secrets",
                "the check creates no key, no identity and no database");
    }

    [Fact]
    public void ACheck_WhoseLockCannotBeTaken_Fails()
    {
        var folder = TestFolders.New("selfcheck");
        var platform = new MisbehavingPlatform(PlatformSelector.Create(folder)) { RefuseLock = true };

        var (exit, output) = Run(platform, Check(folder));

        exit.Should().Be(1);
        output.Should().Contain("FAIL one copy").And.Contain("lock could not be taken");
    }

    [Fact]
    public void ACheck_WhoseSecondTryIsNotRefused_Fails()
    {
        var folder = TestFolders.New("selfcheck");
        var platform = new MisbehavingPlatform(PlatformSelector.Create(folder)) { AlwaysGrantLock = true };

        var (exit, output) = Run(platform, Check(folder));

        exit.Should().Be(1);
        output.Should().Contain("FAIL one copy").And.Contain("second try was NOT refused");
    }

    [Fact]
    public void ACheck_WhoseSignalNeverArrives_Fails()
    {
        var folder = TestFolders.New("selfcheck");
        var platform = new MisbehavingPlatform(PlatformSelector.Create(folder)) { SignalAcceptedButLost = true };

        var (exit, output) = Run(platform, Check(folder));

        exit.Should().Be(1);
        output.Should().Contain("FAIL one copy").And.Contain("never arrived");
    }

    [Fact]
    public void ACheck_WhoseFolderIsNotAbsolute_OrIsTheFullAppsData_Fails()
    {
        var relative = new MisbehavingPlatform(PlatformSelector.Create(TestFolders.New("selfcheck"))) { FolderOverride = "relative/folder" };
        var (exit1, output1) = Run(relative, Check("relative/folder"));
        exit1.Should().Be(1);
        output1.Should().Contain("FAIL data folder").And.Contain("not an absolute path");

        var fullApp = TestFolders.New("BeeMemoryBankData");
        var (exit2, output2) = Run(PlatformSelector.Create(fullApp), Check(fullApp));
        exit2.Should().Be(1);
        output2.Should().Contain("FAIL data folder").And.Contain("inside the full app's data");
    }

    [Fact]
    public void APlatformItem_ThatFails_FailsTheWholeCheck_AndOneThatThrows_IsAFailureNotACrash()
    {
        var folder = TestFolders.New("selfcheck");

        var (exit, output) = Run(new MisbehavingPlatform(PlatformSelector.Create(folder)) { PlatformItems = [new SelfCheckItem("custom", false, "not right")] }, Check(folder));
        exit.Should().Be(1);
        output.Should().Contain("FAIL custom: not right").And.Contain("SELF-CHECK FAILED (1)");

        var (exit2, output2) = Run(new MisbehavingPlatform(PlatformSelector.Create(folder)) { PlatformItemsThrow = true }, Check(folder));
        exit2.Should().Be(1);
        output2.Should().Contain("FAIL " + PlatformSelector.Create(folder).Name + " items").And.Contain("InvalidOperationException");
    }

    [Fact]
    public void TheSecondStart_IsRefusedByTheRunningCopy_AndItsSignalIsAccepted()
    {
        var folder = TestFolders.New("selfcheck");
        var running = PlatformSelector.Create(folder);
        using var first = running.TryAcquireInstance()!;
        using var shown = new ManualResetEventSlim();
        first.Listen(shown.Set);

        var (exit, output) = Run(PlatformSelector.Create(folder), new StartupOptions(false, folder, SelfCheck: true, SelfCheckSecondStart: true));

        exit.Should().Be(0, output);
        output.Should().Contain("OK   second start").And.Contain("SELF-CHECK PASSED");
        shown.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the running copy was told to show its window");
    }

    [Fact]
    public void TheSecondStart_Fails_WhenNoCopyRuns()
    {
        var folder = TestFolders.New("selfcheck");

        var (exit, output) = Run(PlatformSelector.Create(folder), new StartupOptions(false, folder, SelfCheck: true, SelfCheckSecondStart: true));

        exit.Should().Be(1);
        output.Should().Contain("FAIL second start").And.Contain("lock was free");
    }

    [Fact]
    public void TheSecondStart_Fails_WhenTheRunningCopyDoesNotAnswer()
    {
        var folder = TestFolders.New("selfcheck");
        var platform = new MisbehavingPlatform(PlatformSelector.Create(folder)) { RefuseLock = true, SignalRejected = true };

        var (exit, output) = Run(platform, new StartupOptions(false, folder, SelfCheck: true, SelfCheckSecondStart: true));

        exit.Should().Be(1);
        output.Should().Contain("FAIL second start").And.Contain("NOT accepted");
    }

    [Fact]
    public async Task TheWaitingCheck_HoldsTheLock_UntilAFirstAndRealSecondStartArrives()
    {
        var folder = TestFolders.New("selfcheck");
        var output = new LockedWriter();
        var started = new ManualResetEventSlim();
        var worker = Task.Run(() =>
        {
            started.Set();
            return HeadlessSelfCheck.Run(new StartupOptions(false, folder, SelfCheck: true, SelfCheckWaitSeconds: 30), PlatformSelector.Create(folder), output);
        });
        started.Wait();
        // the second start, as another process would do it (the platform object is a new one over the same folder)
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var second = (Exit: -1, Output: "");
        while (DateTime.UtcNow < deadline)
        {
            if (output.ToString().Contains("HOLDING"))
            {
                second = Run(PlatformSelector.Create(folder), new StartupOptions(false, folder, SelfCheck: true, SelfCheckSecondStart: true));
                break;
            }
            Thread.Sleep(50);
        }

        second.Exit.Should().Be(0, second.Output);
        (await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(worker, "the waiting check ends once the second start came");
        (await worker).Should().Be(0, output.ToString());
        output.ToString().Should().Contain("a second PROCESS was refused and its signal arrived");
    }

    /// <summary>A writer that one thread can fill while another reads it.</summary>
    private sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();
        public override void Write(char value) { lock (_gate) base.Write(value); }
        public override void Write(string? value) { lock (_gate) base.Write(value); }
        public override void WriteLine(string? value) { lock (_gate) base.WriteLine(value); }
        public override string ToString() { lock (_gate) return base.ToString(); }
    }

    /// <summary>The real platform with one defect switched on at a time.</summary>
    private sealed class MisbehavingPlatform(IBlindDesktopPlatform inner) : IBlindDesktopPlatform
    {
        private int _tries;
        public bool RefuseLock { get; init; }
        public bool AlwaysGrantLock { get; init; }
        public bool SignalAcceptedButLost { get; init; }
        public bool SignalRejected { get; init; }
        public string? FolderOverride { get; init; }
        public IReadOnlyList<SelfCheckItem>? PlatformItems { get; init; }
        public bool PlatformItemsThrow { get; init; }

        public string Name => inner.Name;
        public IBlindPaths Paths => FolderOverride is null ? inner.Paths : new FixedPaths(FolderOverride);

        public void AddSeams(IServiceCollection services)
        {
            inner.AddSeams(services);
        }

        public IInstanceGuard? TryAcquireInstance()
        {
            if (RefuseLock) return null;
            if (AlwaysGrantLock) return new LostGuard();
            // the first try gets a lock whose listener never hears anything; later tries are refused, as they should be
            if (SignalAcceptedButLost) return Interlocked.Increment(ref _tries) == 1 ? new LostGuard() : null;
            return inner.TryAcquireInstance();
        }

        public bool SignalRunningInstance() => SignalRejected ? false : SignalAcceptedButLost || AlwaysGrantLock || inner.SignalRunningInstance();

        public IReadOnlyList<SelfCheckItem> SelfCheck() => PlatformItemsThrow ? throw new InvalidOperationException("the platform's own check blew up") : PlatformItems ?? [];
    }

    /// <summary>A guard that takes the lock and never hears a signal.</summary>
    private sealed class LostGuard : IInstanceGuard
    {
        public void Listen(Action onActivate) { }
        public void Dispose() { }
    }

    private sealed class FixedPaths(string folder) : IBlindPaths
    {
        public string DataDirectory => folder;
        public string DatabasePath => Path.Combine(folder, "beememorybank.db");
    }

}
