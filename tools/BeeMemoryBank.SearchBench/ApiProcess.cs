using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BeeMemoryBank.SearchBench;

/// <summary>
/// Owns the lifetime of a real <c>BeeMemoryBank.Api</c> child process launched against the
/// benchmark scratch data directory. The Api is driven as a black-box HTTP server (the same code
/// path a real deployment serves), NOT an in-process TestServer.
///
/// <para><b>Teardown guarantee:</b> the Api is started with <c>BMB_STDIN_LIFELINE=1</c>, so closing
/// its redirected stdin triggers a graceful <c>IHostApplicationLifetime.StopApplication</c>
/// (session lock + DEK wipe). If the graceful stop doesn't complete within a bounded timeout, the
/// process is hard-killed (entire tree) followed by a bounded <c>WaitForExit</c> — per AGENTS.md
/// the <c>Kill</c>+bounded-wait pattern is the safe shape on every OS.</para>
/// </summary>
internal sealed class ApiProcess : IAsyncDisposable, IDisposable
{
    private readonly string _dataPath;
    private readonly string _repoRoot;
    private readonly string _internalKey;
    private readonly string _logDir;
    private Process? _process;
    private StreamWriter? _stdoutLog;
    private StreamWriter? _stderrLog;
    private bool _disposed;

    public string BaseUrl { get; }
    public int Port { get; }
    public string StdoutLogPath { get; }
    public string StderrLogPath { get; }

    private ApiProcess(string repoRoot, string dataPath, int port, string internalKey, string logDir)
    {
        _repoRoot = repoRoot;
        _dataPath = dataPath;
        _internalKey = internalKey;
        _logDir = logDir;
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}";
        StdoutLogPath = Path.Combine(logDir, "api-stdout.log");
        StderrLogPath = Path.Combine(logDir, "api-stderr.log");
    }

    /// <summary>Builds the Api project (if its binary is missing), launches it, and returns once <c>/health</c> is OK.</summary>
    public static async Task<ApiProcess> StartAsync(string repoRoot, string dataPath, string internalKey, string logDir,
        TextWriter progress, CancellationToken ct)
    {
        Directory.CreateDirectory(logDir);

        var (exePath, dllPath, workingDir) = await EnsureApiBuiltAsync(repoRoot, progress, ct);
        int port = AllocateFreeLoopbackPort();
        var api = new ApiProcess(repoRoot, dataPath, port, internalKey, logDir);

        await api.LaunchAsync(exePath, dllPath, workingDir, progress, ct);
        try
        {
            // Health timeout covers a truly cold first launch: Debug-build JIT of every assembly
            // (the Api is built in Debug here) plus migration + folder bootstrap. On a warm file
            // cache this is ~1s; on a cold first launch under load it can be much longer.
            var healthy = await api.WaitForHealthAsync(TimeSpan.FromSeconds(300), progress, ct);
            if (!healthy)
            {
                await api.DumpTailToAsync(progress);
                throw new InvalidOperationException(
                    $"Api did not become healthy at {api.BaseUrl}/health within 300s. " +
                    $"See {api.StdoutLogPath} and {api.StderrLogPath}.");
            }
            progress.WriteLine($"  Api healthy at {api.BaseUrl}.");
        }
        catch
        {
            api.Dispose();
            throw;
        }
        return api;
    }

    private async Task LaunchAsync(string exePath, string dllPath, string workingDir, TextWriter progress, CancellationToken ct)
    {
        _stdoutLog = new StreamWriter(StdoutLogPath, append: false) { AutoFlush = true };
        _stderrLog = new StreamWriter(StderrLogPath, append: false) { AutoFlush = true };

        // Prefer the native apphost (single direct child process). Fall back to `dotnet <dll>` when
        // no apphost exists (non-Windows publish-less builds, or a FrameworkDependent layout).
        string fileName;
        string args;
        if (File.Exists(exePath))
        {
            fileName = exePath;
            args = "";
        }
        else
        {
            fileName = Environment.ProcessPath ?? "dotnet";
            args = $"\"{dllPath}\"";
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Drive the same code path `dotnet run` would, minus its process-tree mess.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        psi.Environment["BMB_DATA_PATH"] = _dataPath;
        // Harness authenticates explicitly via X-Internal-Key; the auto-generated .internal-key
        // path is bypassed entirely by setting BMB_INTERNAL_KEY ourselves.
        psi.Environment["BMB_INTERNAL_KEY"] = _internalKey;
        // Graceful shutdown hook: closing the child's redirected stdin triggers StopApplication().
        psi.Environment["BMB_STDIN_LIFELINE"] = "1";
        // Disable mDNS announce + sync scheduler noise — they only add startup work and log spam.
        psi.Environment["BMB_MDNS_PORT"] = "0";
        // A ready-file isn't needed (we poll /health); don't write one.
        psi.Environment.Remove("BMB_READY_FILE");

        progress.WriteLine($"  launching Api: {fileName} {args}");
        progress.WriteLine($"  ASPNETCORE_URLS={BaseUrl}  BMB_DATA_PATH={_dataPath}");

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) _stdoutLog.WriteLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _stderrLog.WriteLine(e.Data); };

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start the Api child process.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await progress.WriteLineAsync($"  Api PID {_process.Id} started; waiting for /health...");
    }

    // NOTE: this method MUST be `async` (not a sync method returning the inner task). A plain
    // `using var http` in a non-async method disposes the HttpClient the instant the inner task
    // first awaits and yields back — i.e. before /health is ever reached — which silently turns
    // every probe into a TaskCanceledException (mid-flight dispose) followed by
    // ObjectDisposedException. Keeping it async makes the using's scope span the whole poll.
    private async Task<bool> WaitForHealthAsync(TimeSpan timeout, TextWriter progress, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        return await PollHealthAsync(http, timeout, progress, ct);
    }

    private async Task<bool> PollHealthAsync(HttpClient http, TimeSpan timeout, TextWriter progress, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var url = $"{BaseUrl}/health";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int attempts = 0;
        bool printedFirstFail = false;
        while (!cts.IsCancellationRequested)
        {
            attempts++;
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    sw.Stop();
                    progress.WriteLine($"  /health OK after {sw.Elapsed.TotalSeconds:0.0}s ({attempts} attempts).");
                    return true;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return false; }
            catch (Exception ex) when (!printedFirstFail)
            {
                // Surface the very first probe failure's detail — the earlier "silent swallow"
                // version hid a real bug (the disposed-HttpClient one above) for an entire run.
                printedFirstFail = true;
                progress.WriteLine($"  /health probe {attempts} failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* transient: not up yet, keep polling */ }
            if (attempts % 40 == 0) // ~every 10s at 250ms cadence
                progress.WriteLine($"  still waiting for /health after {sw.Elapsed.TotalSeconds:0.0}s...");
            try { await Task.Delay(250, cts.Token); } catch { return false; }
        }
        return false;
    }


    /// <summary>Graceful stop via stdin EOF, then hard-kill + bounded wait. Safe to call multiple times.</summary>
    public async Task StopAsync(TimeSpan gracefulTimeout, TextWriter? progress)
    {
        var proc = _process;
        if (proc == null) return;

        try
        {
            if (proc.HasExited)
            {
                progress?.WriteLine($"  Api PID {proc.Id} already exited (code {proc.ExitCode}).");
                return;
            }

            // 1. Graceful: close stdin → StdinLifeline EOF → StopApplication → DEK wipe + Kestrel drain.
            try
            {
                proc.StandardInput.Close();
            }
            catch (Exception ex)
            {
                progress?.WriteLine($"  (stdin close failed: {ex.Message}; will hard-kill)");
            }

            try
            {
                if (await WaitForExitAsync(proc, gracefulTimeout))
                {
                    progress?.WriteLine($"  Api PID {proc.Id} shut down gracefully within {gracefulTimeout.TotalSeconds:0}s.");
                    return;
                }
            }
            catch (Exception ex) { progress?.WriteLine($"  (graceful wait threw: {ex.Message})"); }

            // 2. Force: kill the whole tree, then bounded wait (AGENTS.md kill+wait pattern).
            progress?.WriteLine($"  Api PID {proc.Id} did not exit gracefully; force-killing the tree.");
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) { progress?.WriteLine($"  (Kill failed: {ex.Message})"); }

            try
            {
                if (await WaitForExitAsync(proc, TimeSpan.FromSeconds(10)))
                    progress?.WriteLine($"  Api PID {proc.Id} terminated after force-kill.");
                else
                    progress?.WriteLine($"  WARNING: Api PID {proc.Id} still alive 10s after force-kill.");
            }
            catch (Exception ex) { progress?.WriteLine($"  (post-kill wait threw: {ex.Message})"); }
        }
        finally
        {
            // The Api process has exited (or been given up on) — release the redirected stdout/stderr
            // log files NOW, while StopAsync is awaited and BEFORE the caller deletes the scratch
            // data/log dir. Otherwise the StreamWriter handles keep those files locked and the
            // cleanup delete fails with "being used by another process". (Dispose() also closes them,
            // but the caller's `await using` disposes only after its own cleanup block runs.)
            CloseLogs();
        }
    }

    private void CloseLogs()
    {
        try { _stdoutLog?.Dispose(); } catch { }
        try { _stderrLog?.Dispose(); } catch { }
        _stdoutLog = null;
        _stderrLog = null;
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { await proc.WaitForExitAsync(cts.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private async Task DumpTailToAsync(TextWriter progress)
    {
        foreach (var (label, path) in new[] { ("stdout", StdoutLogPath), ("stderr", StderrLogPath) })
        {
            if (!File.Exists(path)) continue;
            progress.WriteLine($"--- Api {label} (last 40 lines) ---");
            try
            {
                var lines = await File.ReadAllLinesAsync(path);
                var tail = lines.Length > 40 ? lines[^40..] : lines;
                foreach (var line in tail)
                    progress.WriteLine($"  {line}");
            }
            catch (Exception ex) { progress.WriteLine($"  (could not read {path}: {ex.Message})"); }
        }
    }

    /// <summary>
    /// Builds the Api project if the apphost/dll is missing. Returns the resolved exe, dll, and
    /// working directory (the bin output folder so model.onnx/content files are alongside).
    /// </summary>
    private static async Task<(string exe, string dll, string workingDir)> EnsureApiBuiltAsync(
        string repoRoot, TextWriter progress, CancellationToken ct)
    {
        var proj = Path.Combine(repoRoot, "server", "BeeMemoryBank.Api", "BeeMemoryBank.Api.csproj");
        if (!File.Exists(proj))
            throw new InvalidOperationException($"Api project not found at {proj}. Is the repo root correct?");

        // The TFM is fixed at net10.0 by Directory.Build.props. Configuration: Debug.
        var outDir = Path.Combine(repoRoot, "server", "BeeMemoryBank.Api", "bin", "Debug", "net10.0");
        string exeName = OperatingSystem.IsWindows() ? "BeeMemoryBank.Api.exe" : "BeeMemoryBank.Api";
        var exePath = Path.Combine(outDir, exeName);
        var dllPath = Path.Combine(outDir, "BeeMemoryBank.Api.dll");

        if (File.Exists(exePath) || File.Exists(dllPath))
            return (exePath, dllPath, outDir);

        progress.WriteLine($"  Api binary not found under {outDir}; building {proj}...");
        await BuildProjectAsync(proj, repoRoot, progress, ct);
        if (!File.Exists(exePath) && !File.Exists(dllPath))
            throw new InvalidOperationException($"Api build completed but produced no output under {outDir}.");
        return (exePath, dllPath, outDir);
    }

    /// <summary>Builds a single project quietly. Used for both the Api and bmb-seedgen.</summary>
    public static async Task BuildProjectAsync(string projPath, string repoRoot, TextWriter progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projPath}\" -c Debug --nologo -clp:ErrorsOnly",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'dotnet build'.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode != 0)
        {
            await progress.WriteLineAsync($"  build FAILED (exit {p.ExitCode}) for {projPath}");
            if (!string.IsNullOrWhiteSpace(stdout)) await progress.WriteLineAsync(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) await progress.WriteLineAsync(stderr);
            throw new InvalidOperationException($"Build failed for {projPath} (exit {p.ExitCode}).");
        }
    }

    /// <summary>Ensures bmb-seedgen has been built; returns the dll path (invoked via `dotnet &lt;dll&gt;`).</summary>
    public static async Task<string> EnsureSeedGenBuiltAsync(string repoRoot, TextWriter progress, CancellationToken ct)
    {
        var proj = Path.Combine(repoRoot, "tools", "BeeMemoryBank.SeedGen", "BeeMemoryBank.SeedGen.csproj");
        if (!File.Exists(proj))
            throw new InvalidOperationException($"SeedGen project not found at {proj}.");
        var outDir = Path.Combine(repoRoot, "tools", "BeeMemoryBank.SeedGen", "bin", "Debug", "net10.0");
        string exeName = OperatingSystem.IsWindows() ? "bmb-seedgen.exe" : "bmb-seedgen";
        var exePath = Path.Combine(outDir, exeName);
        var dllPath = Path.Combine(outDir, "bmb-seedgen.dll");

        if (File.Exists(exePath) || File.Exists(dllPath))
            return File.Exists(exePath) ? exePath : dllPath;

        progress.WriteLine($"  SeedGen binary not found under {outDir}; building {proj}...");
        await BuildProjectAsync(proj, repoRoot, progress, ct);
        if (!File.Exists(exePath) && !File.Exists(dllPath))
            throw new InvalidOperationException($"SeedGen build produced no output under {outDir}.");
        return File.Exists(exePath) ? exePath : dllPath;
    }

    private static int AllocateFreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        try { l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Best-effort synchronous stop if the caller forgot to await StopAsync (e.g. on exception).
        var proc = _process;
        if (proc != null && !proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(5000); } catch { }
        }
        try { _stdoutLog?.Dispose(); } catch { }
        try { _stderrLog?.Dispose(); } catch { }
        try { proc?.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        // Best-effort graceful stop on async disposal path (caller forgot StopAsync).
        try { await StopAsync(TimeSpan.FromSeconds(10), progress: null); }
        catch { /* swallow — disposal must not throw */ }
        Dispose();
    }
}
