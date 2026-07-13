using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Outcome of a start-or-attach attempt. Deliberately UI-agnostic: it carries only the
/// data the caller (MainWindow) needs to decide what to render — never any Avalonia types
/// or a reference to a specific <c>Window</c>/<c>Dispatcher</c>.
/// </summary>
public sealed record NodeLifecycleResult
{
    public bool Success { get; init; }
    public string? FrontUrl { get; init; }
    public string? ErrorMessage { get; init; }

    public static NodeLifecycleResult Ok(string frontUrl)
        => new() { Success = true, FrontUrl = frontUrl };

    public static NodeLifecycleResult Error(string message)
        => new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// Hosts (spawns) or attaches to a BeeMemoryBank.Node (bmbd) process for a given data
/// directory, and stops it again. Extracted verbatim from MainWindow.HostOrAttachAsync /
/// StopNodeProcess — this class does NOT know about Avalonia, <c>Window</c> or
/// <c>Dispatcher</c>; it reports textual progress via <see cref="IProgress{T}"/> and
/// returns a <see cref="NodeLifecycleResult"/>. The caller decides what to show on screen.
/// </summary>
public sealed class NodeLifecycleService
{
    private Process? _nodeProcess;

    /// <summary>
    /// Resolves the data directory, attempts rescue of legacy data, probes for an already
    /// running node via <c>.runtime.json</c> + <c>/node/status</c> (attach), and otherwise
    /// launches a new bmbd process and polls until its front is ready. The returned result
    /// is never <c>null</c>: either success with a <see cref="NodeLifecycleResult.FrontUrl"/>,
    /// or a failure with a <see cref="NodeLifecycleResult.ErrorMessage"/>.
    /// </summary>
    /// <param name="dataDir">Vault/data directory to host for. Never hardcoded here.</param>
    /// <param name="progress">Optional receiver for human-readable status lines.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NodeLifecycleResult> StartOrAttachAsync(
        string dataDir,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        try
        {
            progress?.Report("Resolving data directory...");
            Directory.CreateDirectory(dataDir);

            // §73-89: Rescue legacy data (from <AppContext.BaseDirectory>\data) STRICTLY before
            // any host-or-attach logic. Legacy path = the old pre-Stage-1 default location.
            progress?.Report("Checking for legacy data to rescue...");
            var legacyDataDir = Path.Combine(AppContext.BaseDirectory, "data");
            var rescueResult = LegacyDataRescue.TryRescue(legacyDataDir, dataDir);
            if (rescueResult.Outcome == RescueOutcome.LegacyFoundButRescueFailed)
            {
                return NodeLifecycleResult.Error(
                    $"Legacy data rescue failed — cannot start with an empty vault.\n\n" +
                    $"Source: {legacyDataDir}\n" +
                    $"Reason: {rescueResult.Message}\n\n" +
                    "Please free up the data directory (e.g. stop any running BeeMemoryBank node) and retry.");
            }

            progress?.Report("Probing existing node instance...");
            var runtimeJsonPath = Path.Combine(dataDir, ".runtime.json");

            bool attached = false;
            string? frontUrl = null;

            if (File.Exists(runtimeJsonPath))
            {
                RuntimeDescriptor? descriptor = null;
                try
                {
                    var json = await File.ReadAllTextAsync(runtimeJsonPath, ct);
                    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                    descriptor = JsonSerializer.Deserialize<RuntimeDescriptor>(json, options);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to parse existing runtime json: {ex.Message}");
                }

                if (descriptor != null && descriptor.Pid > 0 && !string.IsNullOrEmpty(descriptor.FrontUrl))
                {
                    // Check if PID is running
                    bool isProcessRunning = false;
                    try
                    {
                        using var proc = Process.GetProcessById(descriptor.Pid);
                        isProcessRunning = !proc.HasExited;
                    }
                    catch (ArgumentException) { }

                    if (isProcessRunning)
                    {
                        progress?.Report("Probing existing node status endpoint...");
                        bool probeOk = false;
                        try
                        {
                            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                            var response = await client.GetAsync($"{descriptor.FrontUrl.TrimEnd('/')}/node/status", ct);
                            if (response.IsSuccessStatusCode)
                            {
                                probeOk = true;
                            }
                        }
                        catch { }

                        if (probeOk)
                        {
                            attached = true;
                            frontUrl = descriptor.FrontUrl;
                            progress?.Report("Attached to running node!");
                        }
                    }
                }
            }

            if (!attached)
            {
                progress?.Report("Locating BeeMemoryBank.Node executable...");
                var nodeExePath = ResolveNodeExePath();

                progress?.Report("Starting background node service...");

                // Clean up any stale runtime.json to avoid parsing old files
                if (File.Exists(runtimeJsonPath))
                {
                    try { File.Delete(runtimeJsonPath); } catch { }
                }

                // bmbd's own --auto mode discovers the sibling api/ and web/ folders and
                // wires up ready-files, stdin-lifeline (graceful shutdown + session-lock
                // hygiene on stop) and forwarded-headers itself - no need to hand-author a
                // node.config.json here, and no risk of drifting from bmbd's own conventions
                // (e.g. hardcoding ports that collide with the standalone/Docker defaults).
                var startInfo = new ProcessStartInfo
                {
                    FileName = nodeExePath,
                    Arguments = $"--auto --data \"{dataDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(nodeExePath)
                };

                // Simple log rotation: leave at most 10 bmbd-*.log files
                try
                {
                    var logsDir = BmbPaths.LogsDir;
                    var existingLogs = Directory.GetFiles(logsDir, "bmbd-*.log")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .ToList();

                    if (existingLogs.Count >= 10)
                    {
                        for (int i = 9; i < existingLogs.Count; i++)
                        {
                            try
                            {
                                existingLogs[i].Delete();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to delete old bmbd log file: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to rotate logs: {ex.Message}");
                }

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var logPath = Path.Combine(BmbPaths.LogsDir, $"bmbd-{timestamp}.log");
                var logLock = new object();

                var proc = new Process { StartInfo = startInfo };

                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (logLock)
                        {
                            try
                            {
                                File.AppendAllText(logPath, $"[OUT] {e.Data}{Environment.NewLine}");
                            }
                            catch { }
                        }
                    }
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (logLock)
                        {
                            try
                            {
                                File.AppendAllText(logPath, $"[ERR] {e.Data}{Environment.NewLine}");
                            }
                            catch { }
                        }
                    }
                };

                if (!proc.Start())
                {
                    throw new Exception("Failed to start BeeMemoryBank.Node process.");
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                _nodeProcess = proc;

                // Start polling for .runtime.json
                progress?.Report("Waiting for node services to start (up to 60s)...");
                var stopwatch = Stopwatch.StartNew();

                while (stopwatch.Elapsed.TotalSeconds < 60 && !ct.IsCancellationRequested)
                {
                    if (proc.HasExited)
                    {
                        throw new Exception($"Node process exited prematurely with code {proc.ExitCode}. Check bmbd logs.");
                    }

                    if (File.Exists(runtimeJsonPath))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(runtimeJsonPath, ct);
                            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                            var newDescriptor = JsonSerializer.Deserialize<RuntimeDescriptor>(json, options);

                            if (newDescriptor != null && !string.IsNullOrEmpty(newDescriptor.FrontUrl))
                            {
                                // Verify it is actually responding
                                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                                var response = await client.GetAsync($"{newDescriptor.FrontUrl.TrimEnd('/')}/node/status", ct);
                                if (response.IsSuccessStatusCode)
                                {
                                    frontUrl = newDescriptor.FrontUrl;
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // File might be in the middle of being written, ignore and try again
                        }
                    }

                    progress?.Report($"Waiting for node services to start... ({Math.Round(stopwatch.Elapsed.TotalSeconds)}s)");
                    await Task.Delay(500, ct);
                }

                if (frontUrl == null)
                {
                    throw new TimeoutException("Timed out waiting for BeeMemoryBank.Node services to become ready.");
                }
            }

            return NodeLifecycleResult.Ok(frontUrl!);
        }
        catch (Exception ex)
        {
            return NodeLifecycleResult.Error(ex.Message);
        }
    }

    /// <summary>
    /// Stops the hosted node process. Today this is a hard kill of the whole process tree
    /// (verbatim from the previous MainWindow.StopNodeProcess). The
    /// <paramref name="gracefulTimeout"/>/<paramref name="ct"/> parameters are part of the
    /// stable contract for the upcoming graceful-stop work and are intentionally unused
    /// here so the call site does not have to change again.
    /// </summary>
    public Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct)
    {
        if (_nodeProcess != null && !_nodeProcess.HasExited)
        {
            try
            {
                _nodeProcess.Kill(entireProcessTree: true);
                _nodeProcess.Dispose();
                _nodeProcess = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error killing node process: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    private static string ResolveNodeExePath()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. Production published layout: sibling to desktop
        var prodPath = Path.GetFullPath(Path.Combine(baseDir, "..", "bmbd", "BeeMemoryBank.Node.exe"));
        if (File.Exists(prodPath))
        {
            return prodPath;
        }

        // 1b. Packaged (Velopack) layout: vpk requires the main exe at the root of
        // --packDir, so bmbd/api/web/cli ship as subfolders alongside Desktop.exe
        // itself rather than as siblings of a desktop/ folder one level up.
        var packagedPath = Path.GetFullPath(Path.Combine(baseDir, "bmbd", "BeeMemoryBank.Node.exe"));
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

        // 2. Development tree: search up for solution file then down
        var currentDir = new DirectoryInfo(baseDir);
        while (currentDir != null)
        {
            var slnxFile = Path.Combine(currentDir.FullName, "BeeMemoryBank.slnx");
            if (File.Exists(slnxFile))
            {
                var devPath = Path.Combine(currentDir.FullName, "desktop", "BeeMemoryBank.Node", "bin", "Debug", "net10.0", "BeeMemoryBank.Node.exe");
                if (File.Exists(devPath))
                {
                    return Path.GetFullPath(devPath);
                }
                break;
            }
            currentDir = currentDir.Parent;
        }

        // 3. Current folder fallback
        var siblingPath = Path.GetFullPath(Path.Combine(baseDir, "BeeMemoryBank.Node.exe"));
        if (File.Exists(siblingPath))
        {
            return siblingPath;
        }

        throw new FileNotFoundException($"Could not locate BeeMemoryBank.Node.exe. Looked in:\n- {prodPath}\n- (development layout root)\n- {siblingPath}");
    }
}

public record RuntimeDescriptor(
    int Pid,
    string? FrontUrl,
    string Version,
    string Mode
);
