using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// Covers the DESKTOP side of node process lifecycle stop semantics, mirroring the patterns
/// in BeeMemoryBank.Node.Tests/EndToEndIntegrationTests.cs (which already covers bmbd's OWN
/// stdin-EOF graceful shutdown). These tests use the tiny BeeMemoryBank.Node.Tests.StubProcess
/// as a stand-in for a real hosted bmbd so they stay fast and independent of the full
/// Api/Web stack.
///
/// Three scenarios, per the task:
///  - Hosted process that reacts to stdin EOF  → StopAsync closes stdin, process exits
///    GRACEFULLY (well within the timeout, so it was NOT killed).
///  - Hosted process that ignores EOF (hung)   → StopAsync waits the timeout, then hard-kills.
///  - Attached (foreign) process               → StopAsync does NOT touch it at all.
/// </summary>
public class NodeLifecycleServiceTests
{
    private static readonly string StubDllPath = Path.Combine(
        AppContext.BaseDirectory, "BeeMemoryBank.Node.Tests.StubProcess.dll");

    // Native apphost - StartOrAttachAsync sets ProcessStartInfo.FileName directly (no "dotnet"
    // prefix), so TestOnly_NodeExePathOverride needs a directly-executable path. The apphost
    // build output only carries a ".exe" suffix on Windows - on Linux/macOS it's the bare
    // assembly name with the executable bit set.
    private static readonly string StubExePath = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "BeeMemoryBank.Node.Tests.StubProcess.exe" : "BeeMemoryBank.Node.Tests.StubProcess");

    /// <summary>
    /// Launches the stub via `dotnet stub.dll [extraArgs]` with a redirected stdin, and
    /// returns the started process (whose StandardInput the caller owns) plus its OS pid.
    /// </summary>
    private static (Process proc, int pid) LaunchStub(string extraArgs, bool redirectInput)
    {
        if (!File.Exists(StubDllPath))
        {
            throw new FileNotFoundException($"Stub process DLL not found at: {StubDllPath}.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{StubDllPath}\" {extraArgs}".TrimEnd(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        var proc = new Process { StartInfo = psi };
        proc.Start().Should().BeTrue("stub process should start");
        return (proc, proc.Id);
    }

    /// <summary>True iff an OS process with the given pid is still alive.</summary>
    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Best-effort cleanup: kill a pid (and its tree) if it is still alive.</summary>
    private static void KillPidIfAlive(int pid)
    {
        if (!IsPidAlive(pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch { }
    }

    [Fact]
    public async Task StopAsync_HostedProcessReactsToStdinEof_ExitsGracefullyWithinTimeout()
    {
        // The stub's default path blocks on Console.ReadLine() until EOF, then returns 0 —
        // exactly the stdin-lifeline contract a hosted bmbd honors.
        var (proc, pid) = LaunchStub(redirectInput: true, extraArgs: "");

        try
        {
            proc.HasExited.Should().BeFalse("stub should be waiting on stdin");

            var svc = new NodeLifecycleService();
            svc.TestOnly_SetHosted(proc, proc.StandardInput);

            var gracefulTimeout = TimeSpan.FromSeconds(10);
            var sw = Stopwatch.StartNew();
            await svc.StopAsync(gracefulTimeout, CancellationToken.None);
            sw.Stop();

            // The process must be gone...
            IsPidAlive(pid).Should().BeFalse("graceful stdin-EOF shutdown should have terminated the process");

            // ...and it must have gone away WELL before the timeout. If StopAsync had just
            // waited for the timeout and hard-killed, sw.Elapsed would be ~gracefulTimeout;
            // finishing fast proves the stdin-close path actually drove the exit.
            sw.Elapsed.Should().BeLessThan(
                TimeSpan.FromSeconds(4),
                "process should exit on its own once stdin is closed, not wait for the kill timeout");
        }
        finally
        {
            KillPidIfAlive(pid);
        }
    }

    [Fact]
    public async Task StopAsync_HostedProcessIgnoresStdinEof_HardKillsAfterTimeout()
    {
        // --exit-delay-ms makes the stub sleep in a Task.Delay, completely ignoring stdin —
        // so closing stdin does NOT trigger an early exit. This simulates a hung/unresponsive
        // hosted node that must be force-killed.
        var (proc, pid) = LaunchStub(redirectInput: true, extraArgs: "--exit-delay-ms 30000");

        try
        {
            proc.HasExited.Should().BeFalse("stub should be sleeping");

            var svc = new NodeLifecycleService();
            svc.TestOnly_SetHosted(proc, proc.StandardInput);

            var gracefulTimeout = TimeSpan.FromSeconds(2);
            var sw = Stopwatch.StartNew();
            await svc.StopAsync(gracefulTimeout, CancellationToken.None);
            sw.Stop();

            // The process must be gone (killed)...
            IsPidAlive(pid).Should().BeFalse("unresponsive process should be hard-killed after the timeout");

            // ...and it must have taken roughly the full timeout, proving StopAsync actually
            // WAITED for a graceful exit before resorting to the kill (rather than killing
            // immediately). Allow a small margin either side.
            sw.Elapsed.Should().BeGreaterThanOrEqualTo(
                TimeSpan.FromSeconds(1.5),
                "StopAsync should wait the graceful timeout before hard-killing");
        }
        finally
        {
            KillPidIfAlive(pid);
        }
    }

    [Fact]
    public async Task StopAsync_AttachedForeignProcess_DoesNotTouchIt()
    {
        // A foreign process we merely "attached" to (did not spawn). StopAsync must leave it
        // completely alone — no stdin close, no Kill. We verify it is still alive afterwards
        // and then clean it up ourselves.
        var (proc, pid) = LaunchStub(redirectInput: false, extraArgs: "--exit-delay-ms 30000");

        try
        {
            proc.HasExited.Should().BeFalse("foreign stub should be sleeping");

            var svc = new NodeLifecycleService();
            svc.TestOnly_SetAttached(proc);

            await svc.StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

            IsPidAlive(pid).Should().BeTrue(
                "an attached (foreign) process must NOT be killed or otherwise touched by StopAsync");
        }
        finally
        {
            KillPidIfAlive(pid);
        }
    }

    [Fact]
    public async Task StopAsync_NoProcessTracked_IsNoOp()
    {
        // A freshly-constructed service that never hosted or attached anything must stop
        // cleanly without throwing.
        var svc = new NodeLifecycleService();
        var act = () => svc.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartOrAttachAsync_ReadinessTimeout_KillsTheOrphanedProcess()
    {
        // The stub never writes the .runtime.json format StartOrAttachAsync polls for, so it
        // is guaranteed to time out - exercising the real spawn path (not TestOnly_SetHosted)
        // to prove the FAILED start actually kills the process it spawned, rather than leaving
        // it running and untracked (the bug this test guards against: a caller that reverts to
        // a different profile after this failure would otherwise permanently lose the only
        // handle to this orphan).
        if (!File.Exists(StubExePath))
        {
            throw new FileNotFoundException($"Stub process exe not found at: {StubExePath}.");
        }

        var dataDir = Path.Combine(Path.GetTempPath(), "bmb-nls-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var svc = new NodeLifecycleService
        {
            TestOnly_NodeExePathOverride = StubExePath,
            TestOnly_ReadinessTimeout = TimeSpan.FromSeconds(2)
        };

        int? spawnedPid = null;
        svc.TestOnly_OnHostedProcessStarted = pid => spawnedPid = pid;

        try
        {
            var result = await svc.StartOrAttachAsync(dataDir, progress: null, CancellationToken.None);

            result.Success.Should().BeFalse("the stub never satisfies the readiness probe, so this must time out");
            spawnedPid.Should().NotBeNull("the stub process must have been spawned before the timeout");
            IsPidAlive(spawnedPid!.Value).Should().BeFalse(
                "a failed start must kill the process it spawned, not leave it running as an orphan");
        }
        finally
        {
            if (spawnedPid.HasValue)
            {
                KillPidIfAlive(spawnedPid.Value);
            }
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Writes a minimal file with a valid SQLite magic header — what <c>LegacyDataRescue</c>
    /// treats as a "valid legacy database" — mirroring the helper in
    /// BeeMemoryBank.Node.Tests/DataPathResolutionTests.cs.
    /// </summary>
    private static void WriteValidSqliteMagic(string path)
    {
        var magic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(magic, 0, magic.Length);
        fs.Write(new byte[4096], 0, 4096);
    }

    // ── Этап 6 Codex-review fix — rescue must not fire for a non-default vault ─

    /// <summary>
    /// Regression guard for the Этап 6 review finding: <c>StartOrAttachAsync</c> (the DESKTOP
    /// side, used for every profile — not just the default one) must gate
    /// <c>LegacyDataRescue.TryRescue</c> on the target being the canonical default vault, exactly
    /// like <c>desktop/BeeMemoryBank.Node/Program.cs</c> already does. Before this fix, a brand
    /// new non-default profile's FIRST start would find its own vault empty and the legacy DB
    /// still on disk, and would silently copy the legacy data into it — defeating multi-account
    /// isolation for every newly created profile. This test proves a non-default target is left
    /// untouched even though a perfectly valid legacy database is present.
    /// </summary>
    [Fact]
    public async Task StartOrAttachAsync_NonDefaultVaultDir_DoesNotRescueLegacyData_EvenWithValidLegacyDb()
    {
        if (!File.Exists(StubExePath))
        {
            throw new FileNotFoundException($"Stub process exe not found at: {StubExePath}.");
        }

        // 1) Plant a VALID legacy database exactly where StartOrAttachAsync looks for it. This is
        // shared test-output state, so back up any pre-existing content wholesale instead of just
        // tracking whether the directory existed.
        var legacyDir = Path.Combine(AppContext.BaseDirectory, "data");
        string? legacyBackupDir = null;
        if (Directory.Exists(legacyDir))
        {
            legacyBackupDir = legacyDir + ".bak-" + Guid.NewGuid().ToString("N");
            Directory.Move(legacyDir, legacyBackupDir);
        }
        Directory.CreateDirectory(legacyDir);
        WriteValidSqliteMagic(Path.Combine(legacyDir, "beememorybank.db"));

        // 2) Sandbox LOCALAPPDATA so the canonical default vault dir is guaranteed to be a
        // different physical path from our explicit non-default target below.
        var fakeLocalAppData = Path.Combine(Path.GetTempPath(), "bmb-nls-fakela-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeLocalAppData);
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

        var dataDir = Path.Combine(Path.GetTempPath(), "bmb-nls-nondvault-" + Guid.NewGuid().ToString("N"));

        var svc = new NodeLifecycleService
        {
            TestOnly_NodeExePathOverride = StubExePath,
            TestOnly_ReadinessTimeout = TimeSpan.FromSeconds(2)
        };
        int? spawnedPid = null;
        svc.TestOnly_OnHostedProcessStarted = pid => spawnedPid = pid;

        try
        {
            var canonicalDefault = Path.GetFullPath(BmbPaths.DefaultVaultDir);
            string.Equals(
                Path.GetFullPath(dataDir), canonicalDefault,
                StringComparison.OrdinalIgnoreCase).Should().BeFalse(
                    "test setup: the explicit target must NOT equal the canonical default vault");

            // Act — the stub never satisfies the readiness probe, so this times out; the rescue
            // guard runs strictly before that poll, so the timeout still exercises it.
            var result = await svc.StartOrAttachAsync(dataDir, progress: null, CancellationToken.None);
            result.Success.Should().BeFalse("the stub never satisfies the readiness probe, so this must time out");

            // Assert — CRITICAL: rescue must NOT have fired for a non-default vault.
            File.Exists(Path.Combine(dataDir, "beememorybank.db")).Should().BeFalse(
                "rescue must not copy a legacy DB into an explicitly non-default vault");
            File.Exists(Path.Combine(dataDir, "rescued-from.json")).Should().BeFalse(
                "no rescue marker may be written into a non-default vault");
        }
        finally
        {
            if (spawnedPid.HasValue)
            {
                KillPidIfAlive(spawnedPid.Value);
            }
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            try { Directory.Delete(dataDir, recursive: true); } catch { }

            try { if (Directory.Exists(legacyDir)) Directory.Delete(legacyDir, recursive: true); }
            catch { /* best-effort cleanup */ }
            if (legacyBackupDir != null)
            {
                try { Directory.Move(legacyBackupDir, legacyDir); }
                catch { /* best-effort restore */ }
            }
        }
    }

    // ── Final-review fix — surface a conflict-rescue's recovered vault ─────────

    /// <summary>
    /// Regression guard for the final-review finding: when the default vault already holds a
    /// DIFFERENT valid database than the legacy source, <c>LegacyDataRescue.TryRescue</c> copies
    /// the legacy data into a fresh, UNREGISTERED <c>recovered-&lt;date&gt;</c> vault rather than
    /// overwriting the existing one. Before this fix, <c>StartOrAttachAsync</c> silently dropped
    /// that outcome — the data was safe on disk but permanently invisible in Manage Storages.
    /// This proves the recovered vault's path is now surfaced on the result.
    /// </summary>
    [Fact]
    public async Task StartOrAttachAsync_ConflictingLegacyData_SurfacesRecoveredVaultDir()
    {
        if (!File.Exists(StubExePath))
        {
            throw new FileNotFoundException($"Stub process exe not found at: {StubExePath}.");
        }

        var legacyDir = Path.Combine(AppContext.BaseDirectory, "data");
        string? legacyBackupDir = null;
        if (Directory.Exists(legacyDir))
        {
            legacyBackupDir = legacyDir + ".bak-" + Guid.NewGuid().ToString("N");
            Directory.Move(legacyDir, legacyBackupDir);
        }
        Directory.CreateDirectory(legacyDir);
        WriteDistinctSqliteDb(Path.Combine(legacyDir, "beememorybank.db"), marker: 0xAA);

        var fakeLocalAppData = Path.Combine(Path.GetTempPath(), "bmb-nls-recovered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeLocalAppData);
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", fakeLocalAppData);

        var svc = new NodeLifecycleService
        {
            TestOnly_NodeExePathOverride = StubExePath,
            TestOnly_ReadinessTimeout = TimeSpan.FromSeconds(2)
        };
        int? spawnedPid = null;
        svc.TestOnly_OnHostedProcessStarted = pid => spawnedPid = pid;

        try
        {
            // The default vault dir gets created empty by CreateDirectory below, THEN pre-seeded
            // with a DIFFERENT valid DB before StartOrAttachAsync runs, so TryRescue sees "both
            // valid, different" (RescuedToRecoveredVault), not "target empty" (plain rescue).
            var defaultVaultDir = BmbPaths.DefaultVaultDir;
            WriteDistinctSqliteDb(Path.Combine(defaultVaultDir, "beememorybank.db"), marker: 0xBB);

            var result = await svc.StartOrAttachAsync(defaultVaultDir, progress: null, CancellationToken.None);
            result.Success.Should().BeFalse("the stub never satisfies the readiness probe, so this must time out");

            result.RecoveredVaultDir.Should().NotBeNullOrEmpty(
                "a conflict rescue must surface the recovered vault's path so it can be registered");
            result.RecoveredVaultDir!.Should().Contain("recovered-");
            File.Exists(Path.Combine(result.RecoveredVaultDir!, "beememorybank.db")).Should().BeTrue(
                "the legacy DB must have actually landed in the recovered vault");

            // The pre-existing default vault DB must be untouched by the conflict.
            File.Exists(Path.Combine(defaultVaultDir, "beememorybank.db")).Should().BeTrue();
        }
        finally
        {
            if (spawnedPid.HasValue)
            {
                KillPidIfAlive(spawnedPid.Value);
            }
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            try { Directory.Delete(fakeLocalAppData, recursive: true); } catch { }

            try { if (Directory.Exists(legacyDir)) Directory.Delete(legacyDir, recursive: true); }
            catch { /* best-effort cleanup */ }
            if (legacyBackupDir != null)
            {
                try { Directory.Move(legacyBackupDir, legacyDir); }
                catch { /* best-effort restore */ }
            }
        }
    }

    /// <summary>
    /// Writes a valid-but-distinguishable SQLite file (magic header + a byte-marker-filled
    /// payload), mirroring LegacyDataRescueTests' helper of the same name.
    /// </summary>
    private static void WriteDistinctSqliteDb(string path, byte marker = 0xAB)
    {
        var magic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(magic, 0, magic.Length);
        var payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = marker;
        fs.Write(payload, 0, payload.Length);
    }
}
