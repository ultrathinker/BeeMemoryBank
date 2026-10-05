using System.Diagnostics;
using System.Text;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// Runs a child process for a test and returns its exit code and what it printed, WITHOUT ever waiting for the pipes to close.
///
/// <para>Why not <c>ReadToEnd</c> / <c>.Result</c>: a tool such as <c>dotnet publish</c> starts worker processes (MSBuild node reuse, the
/// compiler server) that inherit the redirected stdout and stderr and live on after the tool has finished, up to about 15 minutes. A
/// reader that waits for the end of the stream waits for them: a test that should take seconds took 15 minutes. Here the output is
/// collected as it arrives, the wait ends with the process, and only a short grace period is given to the last lines.</para>
/// </summary>
internal static class ChildProcess
{
    internal sealed record Result(int ExitCode, string Output, string Error, bool TimedOut)
    {
        /// <summary>Both streams, for a failure message.</summary>
        public string Both => Output + Error;
    }

    /// <summary>How long the last lines of output may take to arrive once the process has ended.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Starts <paramref name="start"/> (its streams are redirected here), waits at most <paramref name="timeout"/> for the process to end
    /// (it and its children are killed on a timeout), and returns what it printed so far.
    /// </summary>
    internal static Result Run(ProcessStartInfo start, TimeSpan timeout)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(error, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it ended just now */ }
            return new Result(-1, Snapshot(output), Snapshot(error), TimedOut: true);
        }

        // The process is gone. The parameterless WaitForExit() would now wait until BOTH streams are closed, which a leftover child can
        // delay for a quarter of an hour; give the pending lines a few seconds and go on with what has arrived.
        var drained = Task.Run(() =>
        {
            try { process.WaitForExit(); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* disposed while waiting */ }
        });
        drained.Wait(Grace);

        return new Result(process.ExitCode, Snapshot(output), Snapshot(error), TimedOut: false);
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb) sb.AppendLine(line);
    }

    private static string Snapshot(StringBuilder sb)
    {
        lock (sb) return sb.ToString();
    }

    /// <summary>
    /// A dotnet CLI call that leaves nothing behind: no MSBuild node reuse, no build servers, no shared compiler. Without a worker that
    /// outlives the call there is no pipe left open either, and the machine is not left with idle processes.
    /// </summary>
    internal static ProcessStartInfo DotnetWithoutLeftovers(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = workingDirectory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var arg in new[] { "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false" })
            start.ArgumentList.Add(arg);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        return start;
    }
}
