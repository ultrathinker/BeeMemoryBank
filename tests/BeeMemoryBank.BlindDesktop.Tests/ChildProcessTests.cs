using System.Diagnostics;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The helper that runs <c>dotnet publish</c> for the boundary test must not wait for a worker process that a tool leaves behind with
/// the output pipes still open (it once made a test that takes seconds take 15 minutes). Proved with a stand-in for such a tool: a
/// process that prints a word, ends, and leaves a child running that holds the same pipes.
/// </summary>
public sealed class ChildProcessTests
{
    /// <summary>Prints "started" and ends at once; a child it started keeps running for about ten seconds and holds stdout and stderr.</summary>
    private static ProcessStartInfo ToolThatLeavesAWorkerBehind() =>
        OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "start /b ping -n 11 127.0.0.1 & echo started" } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 10 & echo started" } };

    [Fact]
    public async Task Control_AReaderThatWaitsForTheEndOfTheStream_IsHeldUp_ByTheLeftoverWorker()
    {
        // This is what the old helper did (ReadToEndAsync + .Result after the exit). If this stops holding, the stand-in no longer
        // reproduces the problem and the test below proves nothing.
        var start = ToolThatLeavesAWorkerBehind();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue("the tool itself ends at once");

        var both = Task.WhenAll(output, error);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(4))) == both;

        finished.Should().BeFalse("the leftover child still holds the pipes, so the end of the stream does not come for about ten seconds");
    }

    [Fact]
    public void TheRunner_ReturnsWhenTheProcessEnds_WithItsOutput_WhileAWorkerStillHoldsThePipes()
    {
        var clock = Stopwatch.StartNew();

        var result = ChildProcess.Run(ToolThatLeavesAWorkerBehind(), TimeSpan.FromSeconds(30));

        clock.Stop();
        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain("started");
        clock.Elapsed.Should().BeLessThan(ChildProcess.Grace + TimeSpan.FromSeconds(4), "it does not wait for the worker (about ten seconds)");
    }

    [Fact]
    public void ThePrintedErrorAndTheExitCode_ComeBack()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "echo oops 1>&2 & exit 3" } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "echo oops >&2; exit 3" } };

        var result = ChildProcess.Run(start, TimeSpan.FromSeconds(30));

        result.ExitCode.Should().Be(3);
        result.Error.Should().Contain("oops");
        result.Both.Should().Contain("oops");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public void AProcessThatRunsTooLong_IsStopped_AndReportedAsTimedOut()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe") { ArgumentList = { "-n", "31", "127.0.0.1" } }
            : new ProcessStartInfo("/bin/sleep") { ArgumentList = { "30" } };
        var clock = Stopwatch.StartNew();

        var result = ChildProcess.Run(start, TimeSpan.FromSeconds(1));

        clock.Stop();
        result.TimedOut.Should().BeTrue();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void TheDotnetCall_AsksForNoNodeReuse_NoBuildServer_AndNoSharedCompiler()
    {
        var start = ChildProcess.DotnetWithoutLeftovers("/work", "publish", "x.csproj", "-c", "Release");

        start.FileName.Should().Be("dotnet");
        start.WorkingDirectory.Should().Be("/work");
        start.ArgumentList.Should().StartWith(["publish", "x.csproj", "-c", "Release"]);
        start.ArgumentList.Should().Contain(["--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false"]);
        start.Environment["MSBUILDDISABLENODEREUSE"].Should().Be("1");
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"].Should().Be("0");
        start.Environment["UseSharedCompilation"].Should().Be("false");
    }
}
