using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>One restic invocation, as the backup service asks for it.</summary>
/// <param name="Settings">The job's settings snapshot — repository, password, limits. Taken once
/// per job and never re-read: a settings save while a backup runs must not move its later steps
/// (forget, the recovery-set) to another repository.</param>
/// <param name="Args">Argument list (no shell — <see cref="ProcessStartInfo.ArgumentList"/>).</param>
/// <param name="Job">The running job progress lines are fed to; null for quick calls (list).</param>
/// <param name="Repository">Another repository to run against (the one-off copy's destination);
/// null = the configured one.</param>
/// <param name="FromConfigured">Pass the configured repository as restic's <c>--from-repo</c>
/// (copy and init --copy-chunker-params), with the same password.</param>
public sealed record ResticCall(BlindBackupSettings Settings, IReadOnlyList<string> Args, BlindJobContext? Job = null,
    string? Repository = null, bool FromConfigured = false);

/// <summary>Exit code plus both streams. restic speaks to humans on stderr and to machines on stdout.</summary>
public sealed record ResticResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>restic's exit code 10 — "repository does not exist" (or is unreachable). The caller initializes it once.</summary>
    public bool RepoMissing => ExitCode == 10;
}

public interface IResticRunner
{
    Task<ResticResult> RunAsync(ResticCall call, CancellationToken ct);
}

/// <summary>
/// Runs the restic binary with the repository from <see cref="BlindBackupSettings"/> and the CPU
/// limits of the current mode (plan §7–8): <c>GOMEMLIMIT</c> always, <c>GOMAXPROCS=1</c> and the
/// idle I/O class and lowest CPU priority in Economy, ~90 % of the cores in Fast. Progress lines from
/// <c>restic backup --json</c> are parsed into the job's percent/bytes/ETA.
/// </summary>
public sealed class ResticRunner(
    BlindJobManager jobs,
    string dataPath,
    ILogger<ResticRunner> logger) : IResticRunner
{
    // util-linux and coreutils in the runtime image; absent on Windows and on a bare dev box.
    private const string IonicePath = "/usr/bin/ionice";
    private const string NicePath = "/usr/bin/nice";

    public async Task<ResticResult> RunAsync(ResticCall call, CancellationToken ct)
    {
        var s = call.Settings;
        var mode = jobs.Mode;
        var cacheDir = Path.Combine(dataPath, "blind", "restic-cache");
        Directory.CreateDirectory(cacheDir);
        var psi = BuildStartInfo(call, mode, cacheDir, Environment.ProcessorCount,
            lowPriorityWrapper: !OperatingSystem.IsWindows() && File.Exists(IonicePath) && File.Exists(NicePath));

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {psi.FileName}");
        using var registered = jobs.RegisterChild(proc);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var pumpErr = PumpAsync(proc.StandardError, stderr, null);
        var pumpOut = PumpAsync(proc.StandardOutput, stdout, line => OnLine(line, call.Job));

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation (operator wipe, shutdown): kill the tree and follow the bounded wait —
            // see AGENTS.md on Kill(entireProcessTree) racing on Linux.
            proc.Kill(entireProcessTree: true);
            // Not `ct` here: it is already cancelled, and WaitAsync(…, ct) would return at once.
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { logger.LogWarning("restic {Pid} did not exit 10s after SIGKILL", proc.Id); }
            throw;
        }
        finally
        {
            await Task.WhenAll(pumpOut, pumpErr);
        }

        var result = new ResticResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
        if (result.ExitCode != 0)
            logger.LogInformation("restic {Args} exited {Code}: {Err}", string.Join(' ', call.Args), result.ExitCode,
                Truncate(result.Stderr, 500));
        return result;
    }

    /// <summary>
    /// The restic process as the CPU mode shapes it (plan §8). Always: the repository, password,
    /// cache and <c>GOMEMLIMIT</c>. Economy: <c>GOMAXPROCS=1</c>, and — where the tools exist —
    /// launched through <c>ionice -c 3 nice -n 19</c>, idle I/O class and lowest CPU priority, so
    /// a backup yields the disk and the cores to the sync listener. Both tools exec the next
    /// program, so the PID the job manager SIGSTOPs is restic's own. Fast: ~90 % of the cores at
    /// normal priority.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(ResticCall call, BlindCpuMode mode,
        string cacheDir, int processorCount, bool lowPriorityWrapper)
    {
        var s = call.Settings;
        var args = call.Args;
        var binary = string.IsNullOrWhiteSpace(s.ResticBinary) ? "restic" : s.ResticBinary!;
        var wrap = mode != BlindCpuMode.Fast && lowPriorityWrapper;
        var psi = new ProcessStartInfo(wrap ? IonicePath : binary)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        if (wrap)
            foreach (var a in new[] { "-c", "3", NicePath, "-n", "19", binary })
                psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["RESTIC_REPOSITORY"] = call.Repository ?? s.ResticRepository();
        if (call.FromConfigured)
        {
            // Same password on both sides: the copy is the same backup in another place, and a
            // restore from it needs nothing the recovery-set next to it does not already open.
            psi.Environment["RESTIC_FROM_REPOSITORY"] = s.ResticRepository();
            if (!string.IsNullOrEmpty(s.ResticPassword))
                psi.Environment["RESTIC_FROM_PASSWORD"] = s.ResticPassword;
        }
        if (!string.IsNullOrEmpty(s.ResticPassword))
            psi.Environment["RESTIC_PASSWORD"] = s.ResticPassword;
        psi.Environment["RESTIC_CACHE_DIR"] = cacheDir;
        // GOMEMLIMIT is a soft limit the Go runtime respects under pressure; GOMAXPROCS and the
        // priority are what keep a backup from eating the box the sync listener runs on.
        psi.Environment["GOMEMLIMIT"] = string.IsNullOrWhiteSpace(s.ResticGoMemLimit) ? "512MiB" : s.ResticGoMemLimit;
        psi.Environment["GOMAXPROCS"] = mode == BlindCpuMode.Fast
            ? Math.Max(1, (int)Math.Round(processorCount * 0.9)).ToString()
            : "1";
        if (s.RepoType == BlindRepoType.S3)
        {
            psi.Environment["AWS_ACCESS_KEY_ID"] = s.S3AccessKey;
            psi.Environment["AWS_SECRET_ACCESS_KEY"] = s.S3SecretKey;
            psi.Environment["AWS_DEFAULT_REGION"] = s.S3Region ?? "us-east-1";
        }
        return psi;
    }

    private void OnLine(string line, BlindJobContext? job)
    {
        if (job == null || line.Length == 0 || line[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (!root.TryGetProperty("message_type", out var type)) return;
            switch (type.GetString())
            {
                case "status":
                {
                    double? fraction = root.TryGetProperty("percent_done", out var pct)
                        && pct.ValueKind == JsonValueKind.Number ? pct.GetDouble() : null;
                    long? done = root.TryGetProperty("bytes_done", out var bd)
                        && bd.ValueKind == JsonValueKind.Number ? bd.GetInt64() : null;
                    long? total = root.TryGetProperty("total_bytes", out var tb)
                        && tb.ValueKind == JsonValueKind.Number ? tb.GetInt64() : null;
                    job.ReportProgress(fraction, done, total);
                    break;
                }
                case "summary":
                    job.ReportProgress(1.0, null, null, "snapshot saved");
                    break;
            }
        }
        catch (JsonException)
        {
            // restic interleaves plain-text lines into --json mode on warnings; not progress.
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder into, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            into.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
