using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>How a re-key ended, from the verb's exit code (rekey-offline.md §8.4).</summary>
public enum RekeyOutcome
{
    /// <summary>Exit 0: the vault has its new key.</summary>
    Done,
    /// <summary>Exit 2: the pre-flight refused; nothing was created.</summary>
    PreflightRefused,
    /// <summary>Exit 3, any other code, or the verb could not run: the old vault is still the one in use.</summary>
    Failed,
    /// <summary>Exit 4: the swap finishes at the next start of the node.</summary>
    SwapPending,
    /// <summary>The verb was never started (no CLI); the node was not stopped.</summary>
    NotRun,
}

/// <summary>One <c>--progress-json</c> line: <c>{"step":"...","done":n,"total":n,"note":"..."}</c>.</summary>
public sealed record RekeyProgressLine(string Step, long Done, long Total, string? Note);

public sealed record DesktopRekeyResult
{
    public RekeyOutcome Outcome { get; init; }
    public int? ExitCode { get; init; }
    /// <summary>The report the verb named on its final line.</summary>
    public string? ReportPath { get; init; }
    /// <summary>What the pre-flight refused on, read from the report, for the owner to see at once.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public string Message { get; init; } = "";
    /// <summary>The node's address after the restart; null when it did not start.</summary>
    public string? FrontUrl { get; init; }
    public string? StartError { get; init; }

    /// <summary>The report page, once the node is up on the new vault.</summary>
    public string? ReportUrl => FrontUrl is null || Outcome is not (RekeyOutcome.Done or RekeyOutcome.SwapPending)
        ? null
        : FrontUrl.TrimEnd('/') + "/RekeyReport";
}

/// <summary>Runs a child process with the given stdin and streams its output lines.</summary>
public interface IRekeyProcessRunner
{
    Task<int> RunAsync(string exe, IReadOnlyList<string> args, string stdin, Action<string> onStdout, Action<string> onStderr);
}

/// <summary>
/// The Desktop entry of the offline re-key (rekey-offline.md §7, §8.4): stop the node, run
/// <c>bmb rekey --data &lt;D&gt; --password-stdin --progress-json</c>, relay its progress lines, start the node again
/// and hand back the report page. The password goes on stdin, never on the command line.
///
/// <para>Once the verb has started it is never cancelled from here: killing it in the middle of the swap is the
/// crash the journal is for, not something to cause on purpose. The node is started again whatever the outcome.</para>
/// </summary>
public sealed class DesktopRekeyService
{
    private static readonly TimeSpan StopGracefulTimeout = TimeSpan.FromSeconds(15);

    private readonly INodeLifecycleService _node;
    private readonly IRekeyProcessRunner _runner;
    private readonly Func<string?> _cliPath;

    public DesktopRekeyService(INodeLifecycleService node, IRekeyProcessRunner? runner = null, Func<string?>? cliPath = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _runner = runner ?? new RekeyProcessRunner();
        _cliPath = cliPath ?? ResolveCliPath;
    }

    public async Task<DesktopRekeyResult> RunAsync(string dataDir, string password, bool nodeIsRunning,
        IProgress<RekeyProgressLine>? progress, IProgress<string>? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cli = _cliPath();
        if (cli is null)
            return new DesktopRekeyResult { Outcome = RekeyOutcome.NotRun, Message = "The command-line tool (bmb) was not found next to the app." };

        if (nodeIsRunning)
        {
            status?.Report("Stopping the node...");
            await _node.StopAsync(StopGracefulTimeout, ct).ConfigureAwait(false);
        }

        status?.Report("Re-keying the vault...");
        string? result = null, report = null;
        var errors = new List<string>();
        int? exit = null;
        string? runError = null;
        try
        {
            exit = await _runner.RunAsync(cli, ["rekey", "--data", dataDir, "--password-stdin", "--progress-json"], password + "\n",
                line => OnLine(line, progress, status, ref result, ref report),
                line => { lock (errors) { errors.Add(line); if (errors.Count > 20) errors.RemoveAt(0); } }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            runError = $"The re-key could not run: {ex.Message}";
        }

        var outcome = exit switch
        {
            0 => RekeyOutcome.Done,
            2 => RekeyOutcome.PreflightRefused,
            4 => RekeyOutcome.SwapPending,
            _ => RekeyOutcome.Failed,
        };
        var problems = outcome == RekeyOutcome.PreflightRefused ? ReadBlocking(report) : [];
        var message = runError ?? outcome switch
        {
            RekeyOutcome.Done => "The vault has its new key.",
            RekeyOutcome.PreflightRefused => "The re-key did not start: the check before it found problems. Nothing was changed.",
            RekeyOutcome.SwapPending => "The switch to the re-keyed vault finishes as the node starts.",
            _ => $"The re-key failed (exit code {exit}). The old vault is still the one in use." + Tail(errors),
        };

        // Started again whatever happened: a node that stays down is the one outcome the verb promises never to cause.
        status?.Report("Starting the node...");
        NodeLifecycleResult start;
        try
        {
            start = await _node.StartOrAttachAsync(dataDir, status, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            start = new NodeLifecycleResult { Success = false, ErrorMessage = ex.Message };
        }

        return new DesktopRekeyResult
        {
            Outcome = outcome,
            ExitCode = exit,
            ReportPath = report,
            Problems = problems,
            Message = message,
            FrontUrl = start.Success ? start.FrontUrl : null,
            StartError = start.Success ? null : start.ErrorMessage ?? "The node did not start.",
        };
    }

    private static void OnLine(string line, IProgress<RekeyProgressLine>? progress, IProgress<string>? status, ref string? result, ref string? report)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
            {
                result = r.GetString();
                if (root.TryGetProperty("report", out var p) && p.ValueKind == JsonValueKind.String) report = p.GetString();
                return;
            }
            if (root.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var line2 = new RekeyProgressLine(s.GetString()!, Number(root, "done"), Number(root, "total"),
                    root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
                progress?.Report(line2);
                status?.Report(Describe(line2));
            }
        }
        catch (JsonException)
        {
            // Not a progress line: the verb's own text output, shown as it is.
            status?.Report(line);
        }
    }

    private static long Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static string Describe(RekeyProgressLine p)
    {
        var text = new StringBuilder("Re-keying: ").Append(p.Step);
        if (p.Total > 0) text.Append($" {p.Done}/{p.Total}");
        if (!string.IsNullOrEmpty(p.Note)) text.Append(" — ").Append(p.Note);
        return text.ToString();
    }

    /// <summary>The pre-flight's blocking problems from the report, read loosely: the report is the verb's, and a
    /// report that does not parse must not hide the refusal itself.</summary>
    private static IReadOnlyList<string> ReadBlocking(string? reportPath)
    {
        if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (!doc.RootElement.TryGetProperty("preflight", out var pf) || pf.ValueKind != JsonValueKind.Object
                || !pf.TryGetProperty("blocking", out var blocking) || blocking.ValueKind != JsonValueKind.Array)
                return [];
            return blocking.EnumerateArray().Select(p =>
                $"{Text(p, "table")} {Text(p, "rowKey")}: {Text(p, "problem")}".Trim()).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [$"The report could not be read: {ex.Message}"];
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Tail(List<string> errors)
    {
        lock (errors) return errors.Count == 0 ? "" : "\n\n" + string.Join("\n", errors.TakeLast(5));
    }

    /// <summary>bmb next to the app: the packaged layout (<c>cli\</c> beside Desktop), the published one
    /// (<c>..\cli\</c>), then a development tree.</summary>
    public static string? ResolveCliPath()
    {
        var exe = OperatingSystem.IsWindows() ? "bmb.exe" : "bmb";
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "cli", exe), Path.Combine(baseDir, "..", "cli", exe) })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);

        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) continue;
            var dev = Path.Combine(dir.FullName, "server", "BeeMemoryBank.Cli", "bin", "Debug", "net10.0", exe);
            return File.Exists(dev) ? dev : null;
        }
        return null;
    }
}

/// <summary>The real child process: no window, UTF-8 both ways, stdin written once and closed.</summary>
public sealed class RekeyProcessRunner : IRekeyProcessRunner
{
    public async Task<int> RunAsync(string exe, IReadOnlyList<string> args, string stdin, Action<string> onStdout, Action<string> onStderr)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) onStdout(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onStderr(e.Data); };
        if (!proc.Start()) throw new InvalidOperationException($"{exe} did not start");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync().ConfigureAwait(false);
        proc.WaitForExit(); // flushes the redirected streams' last lines
        return proc.ExitCode;
    }
}
