using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// <c>--self-check --data-dir &lt;scratch folder&gt;</c>: proves that the published app starts and that its composition is sound without
/// showing anything. It creates no window and no tray / menu-bar icon, and it makes and reads NO key: the secret store is replaced by a
/// trap that fails the check if anything calls it, so "no Keychain (DPAPI) item is created or read" is checked, not assumed. It needs a
/// scratch data folder (it refuses the default one, so a check can never touch the real data) and exits 0 when every item is fine, 1
/// otherwise, 2 for a wrong command line.
///
/// <para>Items: the platform that was picked; the data folder rules (absolute, not a folder of the full app, private); the composition
/// is built (the account host over the platform's per-account stores, the device loop, the invitation watch; nothing is started, so no account
/// is opened) and no key-store call happened; the device conditions can be read (their answer is printed, "unknown" counts: the point is that
/// reading works and cannot throw); the state store of the 2.6 account's slot round-trips; the autostart state can be read (it is not changed); the one-copy lock can be taken, a second try in this process
/// is refused, and the "show your window" signal arrives; and the platform's own items. With <c>--self-check-wait N</c> the lock is
/// kept for N more seconds so that a second PROCESS (<c>--self-check-second-start</c>) can be refused for real and its signal received.</para>
/// </summary>
internal static class HeadlessSelfCheck
{
    private static readonly BlindAccountEntry LegacyAccount = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");

    /// <summary>
    /// A finding of the check itself, in its own words ("the lock could not be taken"): shown as it is. It names no folder and no exception text. Any other exception that comes out of a check
    /// is told by its type and its frames only (<see cref="DiagnosticText"/>), like every diagnostic of the head.
    /// </summary>
    private sealed class SelfCheckFinding(string message) : Exception(message);

    private static string Told(Exception ex) => ex is SelfCheckFinding finding ? finding.Message : DiagnosticText.Describe(ex);

    public static int Run(StartupOptions options, IBlindDesktopPlatform platform, TextWriter output)
    {
        if (options.SelfCheckSecondStart) return SecondStart(platform, output);

        var items = new List<SelfCheckItem>();
        void Check(string name, Func<string> body)
        {
            try
            {
                items.Add(new SelfCheckItem(name, true, body()));
            }
            catch (Exception ex)
            {
                items.Add(new SelfCheckItem(name, false, Told(ex)));
            }
        }

        Check("platform", () => $"{platform.Name} ({platform.GetType().Name})");
        Check("data folder", () => DataFolder(platform));

        var trap = new TrappedSecretStoreFactory();
        BlindDesktopRuntime? runtime = null;
        Check("composition", () =>
        {
            runtime = BlindDesktopRuntime.Create(new CheckedPlatform(platform, trap), () => null);
            var names = new[]
            {
                runtime.Accounts.GetType().Name, runtime.Scheduler.GetType().Name, runtime.Watch.GetType().Name,
                runtime.Notifications.GetType().Name, runtime.Autostart.GetType().Name, runtime.Conditions.GetType().Name,
            };
            return "built: " + string.Join(", ", names) + " (not started: no account was opened)";
        });

        if (runtime is not null)
        {
            var rt = runtime;
            Check("state store", () =>
            {
                var state = platform.StateStores.For(LegacyAccount);
                state.Set("bmb.selfcheck", "ok");
                if (state.Get("bmb.selfcheck") != "ok") throw new SelfCheckFinding("a value that was set could not be read back");
                state.Set("bmb.selfcheck", null);
                state.Erase();
                return "set, read back and erased in the scratch folder";
            });
            Check("device conditions", () =>
            {
                static string Word(bool? value) => value switch { true => "yes", false => "no", null => "cannot tell" };
                return $"metered network: {Word(rt.Conditions.IsMetered)}; saving power: {Word(rt.Conditions.IsPowerSaving)} (read only)";
            });
            Check("autostart", () => "login item is " + (rt.Autostart.IsEnabled switch { true => "on", false => "off", null => "unknown" }) + " (read only, not changed)");
        }

        Check("one copy", () => OneCopy(platform, options.SelfCheckWaitSeconds, output, out _));

        foreach (var item in SafePlatformItems(platform)) items.Add(item);

        // After everything above has run: the key store was never called, so no key was created or read.
        Check("no key-store call", () => trap.Calls == 0
            ? "the secret store was constructed but never called (nothing created, nothing read)"
            : throw new SelfCheckFinding($"the secret store was called {trap.Calls} time(s): {trap.LastCall}"));

        if (runtime is not null)
        {
            try { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { items.Add(new SelfCheckItem("stop", false, Told(ex))); }
        }

        var failed = 0;
        foreach (var item in items)
        {
            output.WriteLine($"{(item.Ok ? "OK  " : "FAIL")} {item.Name}: {item.Detail}");
            if (!item.Ok) failed++;
        }
        output.WriteLine(failed == 0 ? "SELF-CHECK PASSED" : $"SELF-CHECK FAILED ({failed})");
        output.Flush();
        return failed == 0 ? 0 : 1;
    }

    /// <summary>What a second start does, for a real second process: it must be refused and its signal must be accepted.</summary>
    private static int SecondStart(IBlindDesktopPlatform platform, TextWriter output)
    {
        using var guard = platform.TryAcquireInstance();
        if (guard is not null)
        {
            output.WriteLine("FAIL second start: the lock was free (no other copy holds it)");
            output.WriteLine("SELF-CHECK FAILED (1)");
            return 1;
        }
        var signalled = platform.SignalRunningInstance();
        output.WriteLine($"{(signalled ? "OK  " : "FAIL")} second start: refused by the running copy; show-window signal {(signalled ? "accepted" : "NOT accepted")}");
        output.WriteLine(signalled ? "SELF-CHECK PASSED" : "SELF-CHECK FAILED (1)");
        output.Flush();
        return signalled ? 0 : 1;
    }

    private static string OneCopy(IBlindDesktopPlatform platform, int waitSeconds, TextWriter output, out bool activated)
    {
        activated = false;
        using var first = platform.TryAcquireInstance() ?? throw new SelfCheckFinding("the lock could not be taken (another copy runs on this folder?)");
        using var shown = new ManualResetEventSlim();
        first.Listen(shown.Set);
        using (var second = platform.TryAcquireInstance())
        {
            if (second is not null) throw new SelfCheckFinding("a second try was NOT refused");
        }
        if (!platform.SignalRunningInstance()) throw new SelfCheckFinding("the show-window signal was not accepted");
        if (!shown.Wait(TimeSpan.FromSeconds(5))) throw new SelfCheckFinding("the show-window signal was accepted but never arrived");
        var detail = "lock taken, a second try refused, the show-window signal arrived";

        if (waitSeconds > 0)
        {
            shown.Reset();
            output.WriteLine($"HOLDING the one-copy lock for up to {waitSeconds} s: start the app again with --self-check-second-start --data-dir <the same folder>");
            output.Flush();
            activated = shown.Wait(TimeSpan.FromSeconds(waitSeconds));
            if (!activated) throw new SelfCheckFinding($"no second start came within {waitSeconds} s");
            detail += "; a second PROCESS was refused and its signal arrived";
        }
        return detail;
    }

    private static string DataFolder(IBlindDesktopPlatform platform)
    {
        var folder = platform.Paths.DataDirectory;
        if (!Path.IsPathRooted(folder)) throw new SelfCheckFinding("the data folder is not an absolute path");
        if (!Directory.Exists(folder)) throw new SelfCheckFinding("the data folder does not exist");
        var segments = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s.StartsWith("BeeMemoryBankData", StringComparison.OrdinalIgnoreCase)))
            throw new SelfCheckFinding("the data folder is inside the full app's data");
        if (!platform.Paths.DatabasePath.StartsWith(folder, StringComparison.Ordinal))
            throw new SelfCheckFinding("the database is outside the data folder");
        return folder;
    }

    private static IReadOnlyList<SelfCheckItem> SafePlatformItems(IBlindDesktopPlatform platform)
    {
        try
        {
            return platform.SelfCheck();
        }
        catch (Exception ex)
        {
            return [new SelfCheckItem(platform.Name + " items", false, Told(ex))];
        }
    }

    /// <summary>The platform as it is, except that the secret store is a trap: the check must not create or read a key.</summary>
    private sealed class CheckedPlatform(IBlindDesktopPlatform inner, IBlindSecretStoreFactory trap) : IBlindDesktopPlatform
    {
        public string Name => inner.Name;
        public IBlindPaths Paths => inner.Paths;
        public IBlindSecretStoreFactory SecretStores => trap;
        public IBlindStateStoreFactory StateStores => inner.StateStores;
        public IBlindDeviceConditions DeviceConditions => inner.DeviceConditions;
        public IBlindAutostart Autostart => inner.Autostart;
        public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
        public bool SignalRunningInstance() => inner.SignalRunningInstance();
    }

    private sealed class TrappedSecretStoreFactory : IBlindSecretStoreFactory
    {
        private readonly TrappedSecretStore _store = new();
        public int Calls => _store.Calls;
        public string? LastCall => _store.LastCall;
        public IBlindSecretStore For(BlindAccountEntry account) => _store;
    }

    private sealed class TrappedSecretStore : IBlindSecretStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastCall { get; private set; }

        private T Trip<T>(string what)
        {
            LastCall = what;
            Interlocked.Increment(ref _calls);
            throw new SelfCheckFinding("the self-check must not touch a key store: " + what);
        }

        public byte[]? LoadIdentitySeed() => Trip<byte[]?>(nameof(LoadIdentitySeed));
        public void SaveIdentitySeed(byte[] seed) => Trip<int>(nameof(SaveIdentitySeed));
        public void SaveBackupKey(byte[] key) => Trip<int>(nameof(SaveBackupKey));
        public byte[]? LoadBackupKey() => Trip<byte[]?>(nameof(LoadBackupKey));
        public void SavePairingSecret(byte[] secret) => Trip<int>(nameof(SavePairingSecret));
        public byte[]? LoadPairingSecret() => Trip<byte[]?>(nameof(LoadPairingSecret));
        public void ClearPairingSecret() => Trip<int>(nameof(ClearPairingSecret));
        public void Clear() => Trip<int>(nameof(Clear));
    }
}
