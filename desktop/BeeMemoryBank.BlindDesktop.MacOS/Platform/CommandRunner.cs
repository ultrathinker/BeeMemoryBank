using System.Diagnostics;
using System.Text;

namespace BeeMemoryBank.BlindDesktop.MacOS;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Runs a system tool and captures its output. A seam: the parsers are tested on captured output, the callers on a fake.</summary>
internal interface ICommandRunner
{
    /// <summary>Never throws: a tool that cannot be started is a result with a non-zero exit code and the reason in the error text.</summary>
    CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>
/// Starts the tool directly (no shell, absolute path, arguments passed one by one), in the C locale so that the English words the parsers
/// look for are what the tool prints, and gives up on it after the timeout.
/// </summary>
internal sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
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
            info.Environment["LC_ALL"] = "C";
            info.Environment["LANG"] = "C";

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
            return new CommandResult(-1, "", ex.Message, TimedOut: false);
        }
    }
}

/// <summary>The absolute paths of the macOS tools the adapters call (not looked up on PATH).</summary>
internal static class MacTools
{
    public const string Netstat = "/usr/sbin/netstat";
    public const string Ifconfig = "/sbin/ifconfig";
    public const string Networksetup = "/usr/sbin/networksetup";
    public const string Osascript = "/usr/bin/osascript";
    public const string Launchctl = "/bin/launchctl";
}
