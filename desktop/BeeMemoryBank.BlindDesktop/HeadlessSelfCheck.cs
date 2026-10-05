using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// <c>--self-check --data-dir &lt;scratch folder&gt;</c>: proves that the published app starts and that its composition is sound without
/// showing anything. It creates no window and no tray / menu-bar icon, and it makes and reads NO key: the secret store is replaced by a
/// trap that fails the check if anything calls it, so "no Keychain (DPAPI) item is created or read" is checked, not assumed. It needs a
/// scratch data folder (it refuses the default one, so a check can never touch the real data) and exits 0 when every item is fine, 1
/// otherwise, 2 for a wrong command line.
///
/// <para>Items: the platform that was picked; the data folder rules (absolute, not a folder of the full app, private); the composition
/// resolves (AppCore + the platform's seams + the host's scheduler, lifecycle and notifications) and no key-store call happened; the
/// device conditions can be read (their answer is printed, "unknown" counts: the point is that reading works and cannot throw); the state
/// store round-trips; the autostart state can be read (it is not changed); the one-copy lock can be taken, a second try in this process
/// is refused, and the "show your window" signal arrives; and the platform's own items. With <c>--self-check-wait N</c> the lock is
/// kept for N more seconds so that a second PROCESS (<c>--self-check-second-start</c>) can be refused for real and its signal received.</para>
/// </summary>
internal static class HeadlessSelfCheck
{
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
                items.Add(new SelfCheckItem(name, false, $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ")));
            }
        }

        Check("platform", () => $"{platform.Name} ({platform.GetType().Name})");
        Check("data folder", () => DataFolder(platform));

        var trap = new TrappedSecretStore();
        BlindDesktopRuntime? runtime = null;
        Check("composition", () =>
        {
            runtime = BlindDesktopRuntime.Create(new CheckedPlatform(platform, trap), () => null, () => { });
            var services = runtime.Services;
            var names = new[]
            {
                services.GetRequiredService<IBlindPaths>().GetType().Name, services.GetRequiredService<IBlindStateStore>().GetType().Name,
                services.GetRequiredService<IBlindAutostart>().GetType().Name,
                services.GetRequiredService<IBlindLifecycle>().GetType().Name, services.GetRequiredService<IBlindNotifications>().GetType().Name,
                services.GetRequiredService<IBlindScheduler>().GetType().Name, runtime.App.GetType().Name,
            };
            return "resolved: " + string.Join(", ", names);
        });

        if (runtime is not null)
        {
            var rt = runtime;
            Check("state store", () =>
            {
                var state = rt.Services.GetRequiredService<IBlindStateStore>();
                state.Set("bmb.selfcheck", "ok");
                if (state.Get("bmb.selfcheck") != "ok") throw new InvalidOperationException("a value that was set could not be read back");
                state.Set("bmb.selfcheck", null);
                state.Erase();
                return "set, read back and erased in the scratch folder";
            });
            Check("autostart", () => "login item is " + (rt.Autostart.IsEnabled switch { true => "on", false => "off", null => "unknown" }) + " (read only, not changed)");
        }

        Check("one copy", () => OneCopy(platform, options.SelfCheckWaitSeconds, output, out _));

        foreach (var item in SafePlatformItems(platform)) items.Add(item);

        // After everything above has run: the key store was never called, so no key was created or read.
        Check("no key-store call", () => trap.Calls == 0
            ? "the secret store was constructed but never called (nothing created, nothing read)"
            : throw new InvalidOperationException($"the secret store was called {trap.Calls} time(s): {trap.LastCall}"));

        if (runtime is not null)
        {
            try { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { items.Add(new SelfCheckItem("stop", false, $"{ex.GetType().Name}: {ex.Message}")); }
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
        using var first = platform.TryAcquireInstance() ?? throw new InvalidOperationException("the lock could not be taken (another copy runs on this folder?)");
        using var shown = new ManualResetEventSlim();
        first.Listen(shown.Set);
        using (var second = platform.TryAcquireInstance())
        {
            if (second is not null) throw new InvalidOperationException("a second try was NOT refused");
        }
        if (!platform.SignalRunningInstance()) throw new InvalidOperationException("the show-window signal was not accepted");
        if (!shown.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("the show-window signal was accepted but never arrived");
        var detail = "lock taken, a second try refused, the show-window signal arrived";

        if (waitSeconds > 0)
        {
            shown.Reset();
            output.WriteLine($"HOLDING the one-copy lock for up to {waitSeconds} s: start the app again with --self-check-second-start --data-dir <the same folder>");
            output.Flush();
            activated = shown.Wait(TimeSpan.FromSeconds(waitSeconds));
            if (!activated) throw new InvalidOperationException($"no second start came within {waitSeconds} s");
            detail += "; a second PROCESS was refused and its signal arrived";
        }
        return detail;
    }

    private static string DataFolder(IBlindDesktopPlatform platform)
    {
        var folder = platform.Paths.DataDirectory;
        if (!Path.IsPathRooted(folder)) throw new InvalidOperationException("the data folder is not an absolute path: " + folder);
        if (!Directory.Exists(folder)) throw new InvalidOperationException("the data folder does not exist: " + folder);
        var segments = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s.StartsWith("BeeMemoryBankData", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("the data folder is inside the full app's data: " + folder);
        if (!platform.Paths.DatabasePath.StartsWith(folder, StringComparison.Ordinal))
            throw new InvalidOperationException("the database is outside the data folder");
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
            return [new SelfCheckItem(platform.Name + " items", false, $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" "))];
        }
    }

    /// <summary>The platform as it is, except that the secret store is a trap: the check must not create or read a key.</summary>
    private sealed class CheckedPlatform(IBlindDesktopPlatform inner, IBlindSecretStore trap) : IBlindDesktopPlatform
    {
        public string Name => inner.Name;
        public IBlindPaths Paths => inner.Paths;
        public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
        public bool SignalRunningInstance() => inner.SignalRunningInstance();

        public void AddSeams(IServiceCollection services)
        {
            inner.AddSeams(services);
            services.AddSingleton(trap);   // registered last, so this is the one that is resolved
        }
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
            throw new InvalidOperationException("the self-check must not touch a key store: " + what);
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
