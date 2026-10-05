using System.Diagnostics;
using System.Runtime.InteropServices;
using BeeMemoryBank.Platforms.Apple.Processes;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Platforms.Apple.Tests;

/// <summary>
/// The tree kill both macOS command runners lean on: when the call returns, the hanging process has exited.
/// The wait is what guarantees that on macOS/Linux (SIGKILL is only sent there); on Windows termination is
/// synchronous anyway, so this runs and means something on every OS.
/// </summary>
public class ProcessTreeKillTests
{
    [Fact]
    public void AHangingProcess_IsExited_WhenTheCallReturns()
    {
        using var process = StartHangingProcess();

        ProcessTreeKill.KillTreeAndWait(process, TimeSpan.FromSeconds(10));

        process.HasExited.Should().BeTrue();
    }

    [Fact]
    public void AProcessThatWasNeverStarted_DoesNotThrow()
    {
        using var process = new Process();

        var act = () => ProcessTreeKill.KillTreeAndWait(process);

        act.Should().NotThrow();
    }

    private static Process StartHangingProcess()
    {
        var info = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false, CreateNoWindow = true };
        var process = Process.Start(info)!;
        process.WaitForExit(500).Should().BeFalse("the started process is meant to hang until it is killed");
        return process;
    }
}
