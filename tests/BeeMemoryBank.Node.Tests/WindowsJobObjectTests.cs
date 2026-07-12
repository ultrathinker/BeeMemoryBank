using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Node.Tests;

public class WindowsJobObjectTests
{
    private readonly string _stubDllPath;

    public WindowsJobObjectTests()
    {
        _stubDllPath = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Node.Tests.StubProcess.dll");
        if (!File.Exists(_stubDllPath))
        {
            throw new FileNotFoundException($"Stub process DLL not found at: {_stubDllPath}. Ensure the test project compiles and references it.");
        }
    }

    [Fact]
    public async Task JobObject_ShouldKillAssignedProcess_WhenJobHandleIsClosed()
    {
        // This test is Windows-specific
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        using var jobObject = new WindowsJobObject();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{_stubDllPath}\" --no-ready-file",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        process.Should().NotBeNull();

        // Assign the process to the Job Object
        jobObject.AssignProcess(process!);

        // Verify the process is currently running
        process!.HasExited.Should().BeFalse();

        // Act
        // Close the job object handle (which should kill the assigned process)
        jobObject.Dispose();

        // Assert
        // The process should be killed by Windows automatically.
        // We wait for it to exit.
        var exited = await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5));
        exited.Should().BeTrue("the child process should have been terminated when the job object handle was closed.");
        process.HasExited.Should().BeTrue();
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>();
        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcs.TrySetResult(true);

        if (process.HasExited)
        {
            return true;
        }

        using var cts = new System.Threading.CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(false));

        return await tcs.Task;
    }
}
