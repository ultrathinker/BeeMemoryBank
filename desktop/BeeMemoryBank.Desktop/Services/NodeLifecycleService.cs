using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Abstraction over <see cref="NodeLifecycleService"/> used by orchestrators (notably
/// <see cref="ProfileSwitchService"/>) so they can be unit-tested with a fake lifecycle
/// instead of a real bmbd process. <see cref="NodeLifecycleService"/> implements this with
/// no behavior change — the members mirror its public surface verbatim.
/// </summary>
public interface INodeLifecycleService
{
    Task<NodeLifecycleResult> StartOrAttachAsync(string dataDir, IProgress<string>? progress, CancellationToken ct);
    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct);
}

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

    /// <summary>
    /// Set when this start rescued conflicting legacy data into a fresh, UNREGISTERED
    /// <c>recovered-&lt;date&gt;</c> vault directory rather than the target vault (see
    /// <see cref="BeeMemoryBank.AppPaths.RescueOutcome.RescuedToRecoveredVault"/>). The caller
    /// should register a profile for this path so the data isn't silently invisible.
    /// </summary>
    public string? RecoveredVaultDir { get; init; }

    public static NodeLifecycleResult Ok(string frontUrl, string? recoveredVaultDir = null)
        => new() { Success = true, FrontUrl = frontUrl, RecoveredVaultDir = recoveredVaultDir };

    public static NodeLifecycleResult Error(string message, string? recoveredVaultDir = null)
        => new() { Success = false, ErrorMessage = message, RecoveredVaultDir = recoveredVaultDir };
}

/// <summary>
/// Hosts (spawns) or attaches to a BeeMemoryBank.Node (bmbd) process for a given data
/// directory, and stops it again. Extracted verbatim from MainWindow.HostOrAttachAsync /
/// StopNodeProcess — this class does NOT know about Avalonia, <c>Window</c> or
/// <c>Dispatcher</c>; it reports textual progress via <see cref="IProgress{T}"/> and
/// returns a <see cref="NodeLifecycleResult"/>. The caller decides what to show on screen.
/// </summary>
public sealed class NodeLifecycleService : INodeLifecycleService
{
    private Process? _nodeProcess;

    // Ownership + graceful-stop state. We only ever Kill / close stdin on a process we
    // ourselves spawned (hosted). When we merely attach to an already-running node, we hold
    // no handle to it at all (see StartOrAttachAsync) and StopAsync must leave it untouched.
    private bool _ownsProcess;
    private StreamWriter? _hostedStdin;

    // Serializes StartOrAttachAsync/StopAsync against each other and against themselves: two
    // overlapping calls on the SAME instance (e.g. a caller invoking SwitchToAsync twice in
    // quick succession before the first finishes) would otherwise race on
    // _nodeProcess/_ownsProcess/_hostedStdin - the second Start's assignment could silently
    // clobber tracking of a process the first Start/Stop is still working with, leaving it
    // unstoppable. A plain lock cannot wrap this method (it awaits), so a 1-slot semaphore
    // provides the same single-flight guarantee across await boundaries.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

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
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StartOrAttachCoreAsync(dataDir, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<NodeLifecycleResult> StartOrAttachCoreAsync(
        string dataDir,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Tracks a process spawned DURING this call so a failure below (timeout, cancellation,
        // premature exit) can kill/dispose it instead of leaving it running but no longer
        // reachable from any future StopAsync call - see the catch block.
        Process? hostedProc = null;
        string? recoveredVaultDir = null;
        try
        {
            progress?.Report("Resolving data directory...");
            Directory.CreateDirectory(dataDir);

            // §73-89: Rescue legacy data (from <AppContext.BaseDirectory>\data) STRICTLY before
            // any host-or-attach logic. Legacy path = the old pre-Stage-1 default location.
            // Mirrors the same guard in desktop/BeeMemoryBank.Node/Program.cs (Fix #4): rescue
            // only ever targets the canonical default vault. Without this guard, ANY profile's
            // first start (not just the default one) would find the target vault empty and the
            // legacy DB still sitting on disk, and would silently copy the legacy data into that
            // profile too — defeating multi-account isolation for every newly created profile.
            var canonicalDefaultDir = Path.GetFullPath(BmbPaths.DefaultVaultDir);
            var isDefaultVaultDir = string.Equals(
                Path.GetFullPath(dataDir), canonicalDefaultDir, StringComparison.OrdinalIgnoreCase);

            if (isDefaultVaultDir)
            {
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

                // The default vault already held a DIFFERENT valid database, so the legacy data
                // was copied into a fresh recovered-<date> vault instead of overwriting it. That
                // vault is NOT registered in profiles.json — surface its path so the caller (which
                // owns ProfileService) can register a profile for it; otherwise the rescued data
                // sits on disk safely but permanently invisible in Manage Storages.
                if (rescueResult.Outcome == RescueOutcome.RescuedToRecoveredVault)
                {
                    recoveredVaultDir = rescueResult.VaultDir;
                }
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
                var nodeExePath = TestOnly_NodeExePathOverride ?? ResolveNodeExePath();

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
                //
                // RedirectStandardInput + BMB_STDIN_LIFELINE opt the hosted process into
                // bmbd's stdin-EOF graceful shutdown: closing our end of the pipe in
                // StopAsync delivers EOF, which bmbd's StdinLifeline turns into a clean
                // session-close + status-file cleanup instead of a hard kill. The env var is
                // set on the CHILD's ProcessStartInfo only (not on this process) so nothing
                // else in Desktop accidentally enters stdin-lifeline mode.
                var startInfo = new ProcessStartInfo
                {
                    FileName = nodeExePath,
                    Arguments = $"--auto --data \"{dataDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(nodeExePath)
                };
                startInfo.EnvironmentVariables["BMB_STDIN_LIFELINE"] = "1";

                // Generate the shared internal-key secret HERE (Desktop is the parent process
                // spawning bmbd) rather than letting bmbd invent its own: bmbd/Program.cs never
                // persists this key to disk by design (see its own comment), so if bmbd picked a
                // key Desktop never learned, ProfileSwitchService's/App.axaml.cs's update-status
                // guard requests would have no way to authenticate and would always fail open.
                // Passing it down via the CHILD's env (inherited by bmbd -> Api/Web) and ALSO
                // setting it on THIS process's own environment - still never touching disk - lets
                // those existing BMB_INTERNAL_KEY env-var lookups find the right value with no
                // further changes on their end. Re-generated (and the process-wide var
                // overwritten) every time a NEW bmbd is hosted, so it always tracks whichever
                // hosted node is currently active.
                var internalKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                startInfo.EnvironmentVariables["BMB_INTERNAL_KEY"] = internalKey;
                Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", internalKey);

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

                hostedProc = proc;
                _nodeProcess = proc;
                _ownsProcess = true;
                _hostedStdin = proc.StandardInput;
                TestOnly_OnHostedProcessStarted?.Invoke(proc.Id);

                // Start polling for .runtime.json
                progress?.Report("Waiting for node services to start (up to 60s)...");
                var stopwatch = Stopwatch.StartNew();

                while (stopwatch.Elapsed < TestOnly_ReadinessTimeout && !ct.IsCancellationRequested)
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

            return NodeLifecycleResult.Ok(frontUrl!, recoveredVaultDir);
        }
        catch (Exception ex)
        {
            if (hostedProc != null)
            {
                // This call spawned hostedProc but failed before returning success (readiness
                // timeout, premature exit, cancellation). Kill it now - otherwise it keeps
                // running as an orphan that no future StopAsync can reach, since Success=false
                // never hands the caller anything to remember it by, and a subsequent
                // StartOrAttachAsync call (e.g. ProfileSwitchService reverting to the previous
                // profile) would overwrite _nodeProcess with a DIFFERENT process, permanently
                // losing the only reference to this one.
                try
                {
                    if (!hostedProc.HasExited)
                    {
                        hostedProc.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception killEx)
                {
                    Debug.WriteLine($"Error killing orphaned node process after failed start: {killEx.Message}");
                }
                try
                {
                    hostedProc.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Debug.WriteLine($"Error disposing orphaned node process after failed start: {disposeEx.Message}");
                }

                if (ReferenceEquals(_nodeProcess, hostedProc))
                {
                    _nodeProcess = null;
                    _ownsProcess = false;
                    _hostedStdin = null;
                }
            }
            // Even though the overall start failed (timeout/premature exit/cancellation), a
            // rescue that already landed in a recovered-<date> vault genuinely happened and
            // that data is on disk right now - surface it so the caller can still register a
            // profile for it instead of losing track of it because the start itself failed.
            return NodeLifecycleResult.Error(ex.Message, recoveredVaultDir);
        }
    }

    /// <summary>
    /// Stops the node. Behavior depends on ownership:
    /// <list type="bullet">
    /// <item><b>Hosted</b> (this service spawned the process): attempt a graceful shutdown
    /// first by closing the child's stdin pipe — EOF triggers bmbd's stdin-lifeline, which
    /// closes the Api session and cleans up status files. If the process does not exit within
    /// <paramref name="gracefulTimeout"/>, fall back to <c>Kill(entireProcessTree: true)</c>
    /// (the previous hard-kill behavior).</item>
    /// <item><b>Attached</b> (we only attached to an already-running node we do not own): do
    /// not touch the foreign process at all — just forget it.</item>
    /// </list>
    /// </summary>
    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct)
    {
        // Same gate as StartOrAttachAsync: serializes Stop against a concurrent Start (or
        // another Stop) on this instance so neither observes the other's half-updated
        // ownership state.
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(gracefulTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(TimeSpan gracefulTimeout, CancellationToken ct)
    {
        var proc = _nodeProcess;
        var stdin = _hostedStdin;
        var owned = _ownsProcess;

        // Clear references up front so a concurrent/re-entrant StopAsync is a no-op.
        _nodeProcess = null;
        _hostedStdin = null;
        _ownsProcess = false;

        if (proc == null || !owned)
        {
            // Attached to a foreign process (or nothing tracked) — leave it running.
            return;
        }

        // Graceful: deliver EOF on the child's stdin → its StdinLifeline runs a clean shutdown.
        try
        {
            stdin?.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error closing node stdin for graceful stop: {ex.Message}");
        }

        try
        {
            // Bound the graceful wait by gracefulTimeout via a linked token. If the caller's
            // own token is already cancelled we still wait up to gracefulTimeout.
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopCts.CancelAfter(gracefulTimeout);
            await proc.WaitForExitAsync(stopCts.Token).ConfigureAwait(false);
            // Exited gracefully within the timeout — nothing more to do.
        }
        catch (OperationCanceledException) when (!proc.HasExited)
        {
            // Timed out (or caller cancelled) while the process was still alive — hard-kill
            // the whole tree as a fallback (today's previous behavior).
            Debug.WriteLine(
                $"Node did not exit gracefully within {gracefulTimeout.TotalSeconds:F0}s; hard-killing process tree.");
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error killing node process after graceful timeout: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during graceful node stop: {ex.Message}");
        }

        try
        {
            proc.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error disposing node process: {ex.Message}");
        }
    }

    // --- Test-only seams -----------------------------------------------------
    // NodeLifecycleServiceTests need to exercise StopAsync against a process it did NOT
    // spawn through StartOrAttachAsync (which would require the full bmbd/api/web stack).
    // These inject a pre-started process directly, mirroring exactly the state the real
    // host/attach paths produce. Marked internal + used only under InternalsVisibleTo.

    /// <summary>Overrides the resolved bmbd executable path so tests can point at a stand-in
    /// process instead of a real bmbd. Null in production (default resolution applies).</summary>
    internal string? TestOnly_NodeExePathOverride { get; set; }

    /// <summary>Overrides the 60s readiness-wait ceiling so timeout tests do not actually wait
    /// 60 seconds. Defaults to the production value.</summary>
    internal TimeSpan TestOnly_ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Fired with the OS pid the moment a hosted process is spawned, before the
    /// readiness wait - lets a test capture the pid of a process that StartOrAttachAsync may
    /// later kill+clear on failure, without racing a same-name process scan.</summary>
    internal Action<int>? TestOnly_OnHostedProcessStarted { get; set; }

    internal void TestOnly_SetHosted(Process proc, StreamWriter stdin)
    {
        _nodeProcess = proc;
        _hostedStdin = stdin;
        _ownsProcess = true;
    }

    internal void TestOnly_SetAttached(Process proc)
    {
        _nodeProcess = proc;
        _hostedStdin = null;
        _ownsProcess = false;
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
