using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
}
