using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BeeMemoryBank.Desktop.MacOS;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    public static CommandResult Failed(string reason) => new(-1, "", reason, TimedOut: false);
}

/// <summary>Runs a system tool and captures its output. A seam: the adapters are tested on a fake, the real thing only on a Mac.</summary>
internal interface ICommandRunner
{
    /// <summary>Never throws: a tool that cannot be started is a result with a non-zero exit code and the reason in the error text.</summary>
    CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>
/// Starts the tool directly - no shell, an absolute path, the arguments passed one by one (so a path with spaces or quotes is one argument
/// and nothing is ever parsed again) - and gives up on it after the timeout. Off macOS it starts nothing and says so.
/// </summary>
internal sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOS()) return CommandResult.Failed("not macOS: " + Path.GetFileName(fileName) + " was not started");
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = info };
            var output = new StringBuilder();
            var error = new StringBuilder();
            using var outputDone = new ManualResetEventSlim();
            using var errorDone = new ManualResetEventSlim();
            process.OutputDataReceived += (_, e) => { if (e.Data is null) outputDone.Set(); else lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is null) errorDone.Set(); else lock (error) error.AppendLine(e.Data); };
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return new CommandResult(-1, "", "timed out", TimedOut: true);
            }
            process.WaitForExit();   // lets the asynchronous readers drain
            outputDone.Wait(TimeSpan.FromSeconds(2));
            errorDone.Wait(TimeSpan.FromSeconds(2));
            lock (output) lock (error)
                return new CommandResult(process.ExitCode, output.ToString(), error.ToString(), TimedOut: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return CommandResult.Failed(ex.Message);
        }
    }
}

/// <summary>The absolute paths of the macOS tools the adapters call (never looked up on PATH).</summary>
internal static class MacTools
{
    public const string Open = "/usr/bin/open";
    public const string Osascript = "/usr/bin/osascript";
    public const string Launchctl = "/bin/launchctl";
    public const string Caffeinate = "/usr/bin/caffeinate";
    public const string Pmset = "/usr/bin/pmset";
}
