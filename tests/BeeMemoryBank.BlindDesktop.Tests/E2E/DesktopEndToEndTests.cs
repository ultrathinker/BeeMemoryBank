using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Tests.E2E;

/// <summary>Runs only when BMB_E2E_DESKTOP=1 (it starts Docker and two full nodes). The default test run lists it as skipped.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BMB_E2E_DESKTOP") != "1")
            Skip = "End-to-end check against a Docker blind node and real full nodes: set BMB_E2E_DESKTOP=1 (see DesktopEndToEndTests).";
    }
}

/// <summary>
/// The whole life of the Windows blind app against real nodes, without a window: AppCore composed with the real Windows adapters (DPAPI, the
/// atomic state file, the paths) in a scratch folder and driven by the real timer scheduler; the listener is the Docker blind node image;
/// the "PC" is a real full node (Api exe). Steps, each logged with PASS or FAIL to the e2e log:
/// <list type="number">
/// <item>start the blind node container and full node A, pair them;</item>
/// <item>the app makes its identity and shows its phone code; A pairs it (POST /api/blind-nodes/android); the app accepts the call code;</item>
/// <item>the scheduler runs the first load; the replica holds A's articles;</item>
/// <item>an article written on A reaches the blind node and then the app by a sync round ("Sync now");</item>
/// <item>"Back up now" (repeated while the computer's sealed copy of the backup key has not arrived by sync) makes a backup;</item>
/// <item>"Save to..." copies it; a FRESH full node B restores it from that file with the master password and shows the articles;</item>
/// <item>"Disconnect and wipe" leaves no key, state or data, and the next start is a fresh first run.</item>
/// </list>
/// Environment: BMB_E2E_DESKTOP=1; BMB_E2E_BLIND_IMAGE (default bmb-blind:f201); BMB_E2E_FULL_EXE (path of BeeMemoryBank.Api.exe);
/// BMB_E2E_WORK (scratch folder, default under the temp folder). Only objects named bmb-e2e-desk-* are created. The test STOPS what it
/// started and removes NOTHING: the container, its two volumes and the scratch folder are left and listed in the log for the owner.
/// </summary>
public sealed class DesktopEndToEndTests
{
    private const string Password = "E2e-test-password-1";
    private const string InternalKey = "e2e-desktop-internal-key";

    [E2EFact]
    public async Task Pair_Load_Sync_Backup_Save_Restore_Wipe()
    {
        var image = Environment.GetEnvironmentVariable("BMB_E2E_BLIND_IMAGE") ?? "bmb-blind:f201";
        var fullExe = Environment.GetEnvironmentVariable("BMB_E2E_FULL_EXE")
            ?? throw new InvalidOperationException("BMB_E2E_FULL_EXE must name BeeMemoryBank.Api.exe");
        var runId = DateTime.UtcNow.ToString("MMddHHmmss");
        var work = Environment.GetEnvironmentVariable("BMB_E2E_WORK") ?? Path.Combine(TestFolders.Root, "e2e-" + runId);
        Directory.CreateDirectory(work);
        using var log = new E2ELog(Path.Combine(work, "e2e.log"));

        var container = $"bmb-e2e-desk-{runId}-blind";
        var volumes = new[] { $"bmb-e2e-desk-{runId}-data", $"bmb-e2e-desk-{runId}-backups" };
        var blindPort = FreePort();
        var consolePort = FreePort();
        FullNode? nodeA = null, nodeB = null;
        BlindDesktopRuntime? app = null;
        try
        {
            // 1. the listener (Docker blind node) and the PC (full node A), paired
            log.Step("starting container " + container + " from " + image);
            var run = Shell.Run("docker", "run", "-d", "--name", container, "--hostname", "bmb-blind",
                "-p", $"127.0.0.1:{blindPort}:5610", "-p", $"127.0.0.1:{consolePort}:5611",
                "-v", volumes[0] + ":/app/data", "-v", volumes[1] + ":/backups",
                "-e", "ASPNETCORE_ENVIRONMENT=Production", "-e", $"BMB_PUBLIC_ADDRESS=https://127.0.0.1:{blindPort}", image);
            log.Check("blind node container starts", run.ExitCode == 0, run.Error);
            await log.WaitFor("blind node healthy", TimeSpan.FromMinutes(3),
                () => Shell.Run("docker", "inspect", "-f", "{{.State.Health.Status}}", container).Output.Trim() == "healthy");

            nodeA = await FullNode.StartAsync(fullExe, Path.Combine(work, "fullA"), FreePort(), log, "A");
            await nodeA.InitializeAsync();
            for (var i = 0; i < 3; i++) await nodeA.WriteArticleAsync($"desktop e2e {i}", $"body {i} " + new string('x', 3000));
            var pairCode = Regex.Match(Shell.Run("docker", "exec", container, "bmb", "blind", "pair-code").Output, @"BMBBLIND1\.[A-Za-z0-9_=.\-]+").Value;
            log.Check("pair code issued", pairCode.Length > 0);
            var blindNodeId = await nodeA.PairBlindNodeAsync(pairCode);
            log.Check("PC pairs with the blind node", blindNodeId != Guid.Empty);
            await nodeA.SignInAsync();
            await log.WaitFor("blind node holds the 3 articles", TimeSpan.FromMinutes(3), () => BlindArticleCount(container) >= 3);

            // 2. the app: identity, phone code, the computer's answer
            var root = Path.Combine(work, "desktop-app");
            var platform = new E2EPlatform(PlatformSelector.Create(root));
            var returned = 0;
            var reports = new List<string>();
            var options = new BlindSchedulerOptions { Tick = TimeSpan.FromSeconds(1) };
            app = BlindDesktopRuntime.Create(platform, () => null, () => Interlocked.Increment(ref returned), scheduler: options,
                displayName: () => "E2E desktop", exporter: new FileExporter(Path.Combine(work, "saved")));
            app.Scheduler.UserJobReported += s => { lock (reports) reports.Add(s); log.Say("user sees: " + s); };
            await app.StartAsync();
            var phoneCode = app.App.PairingCode();
            log.Check("the app shows a phone code", phoneCode?.StartsWith("bmb-blind-phone:?") == true);
            var callCode = await nodeA.PairPhoneAsync(phoneCode!, blindNodeId);
            log.Check("PC pairs the app and returns a call code", callCode is not null);
            log.Check("the app accepts the call code", app.App.AcceptCallCode(callCode!) is null);
            app.Scheduler.RequestHeavy(); // what the Connect button does

            // 3. the scheduler runs the first load
            await log.WaitFor("first load", TimeSpan.FromMinutes(6), () =>
            {
                var s = app.App.GetStatus();
                log.Say($"  status: job={s.ActiveJob ?? "-"} progress={s.JobProgress?.ToString("P0") ?? "-"} loaded={s.InitialLoadDone}");
                return s.InitialLoadDone;
            }, pollSeconds: 2);
            var replica = Path.Combine(root, "beememorybank.db");
            log.Check("the replica holds the 3 articles", ReplicaArticleCount(replica) >= 3, "count=" + ReplicaArticleCount(replica));

            // 4. a later article reaches the app by sync
            await nodeA.WriteArticleAsync("desktop e2e after pairing", "late " + new string('y', 2000));
            await log.WaitFor("blind node holds the 4th article", TimeSpan.FromMinutes(3), () => BlindArticleCount(container) >= 4);
            var syncedBefore = app.App.GetStatus().LastSyncAt;
            app.Scheduler.RequestSync();
            await log.WaitFor("sync round brings the 4th article", TimeSpan.FromMinutes(2), () =>
                ReplicaArticleCount(replica) >= 4
                && app.App.GetStatus().LastSyncAt is { } at && (syncedBefore is null || at > syncedBefore), pollSeconds: 2);
            log.Check("a sync round ran and the replica holds 4 articles", ReplicaArticleCount(replica) >= 4);

            // 5. a backup. It needs the network's recovery box, an integrity anchor (a full node publishes the first one at the end of its first
            //    10-minute tick) and a package of the listener that includes that anchor (the listener renews its package every 30 minutes; the
            //    test restarts ITS OWN container instead of waiting for that). "Back up now" is asked again until a backup exists.
            var restartedAt = DateTime.MinValue;
            await log.WaitFor("a backup is made", TimeSpan.FromMinutes(30), () =>
            {
                if (app.App.GetStatus().Backups.Count > 0) return true;
                app.Scheduler.RequestSync();
                Thread.Sleep(5000);
                string? last;
                lock (reports) last = reports.LastOrDefault(r => r != "Synced.");
                if (last?.Contains("a package that includes", StringComparison.Ordinal) == true && DateTime.UtcNow - restartedAt > TimeSpan.FromMinutes(3))
                {
                    log.Say("the listener still serves an older package: restarting the test's own container so that it builds a fresh one");
                    restartedAt = DateTime.UtcNow;
                    Shell.Run("docker", "restart", container);
                    for (var i = 0; i < 60 && Shell.Run("docker", "inspect", "-f", "{{.State.Health.Status}}", container).Output.Trim() != "healthy"; i++)
                        Thread.Sleep(2000);
                }
                app.Scheduler.RequestBackup();
                return false;
            }, pollSeconds: 10);
            var backup = app.App.GetStatus().Backups.First();
            log.Check("a finished backup exists", backup.Size > 0, backup.Name + " " + backup.Size + " bytes");

            // 6. save it, restore it on a FRESH full node
            var copied = await app.App.ExportBackupAsync(backup.Name);
            var saved = Path.Combine(work, "saved", backup.Name);
            log.Check("Save to... wrote the whole backup", copied == backup.Size && new FileInfo(saved).Length == backup.Size, $"{copied} bytes");
            log.Check("the saved file is a finished backup", AndroidBackupRestore.LooksLikeBackup(saved));

            nodeB = await FullNode.StartAsync(fullExe, Path.Combine(work, "fullB"), FreePort(), log, "B");
            await nodeB.RestoreFromBackupAsync(saved, Password);
            await nodeB.UnlockAsync();
            var titles = await nodeB.ArticleTitlesAsync();
            log.Say("restored node lists: " + string.Join(" | ", titles));
            foreach (var title in new[] { "desktop e2e 0", "desktop e2e 1", "desktop e2e 2", "desktop e2e after pairing" })
                log.Check($"the restored node has \"{title}\"", titles.Contains(title));

            // 7. Disconnect and wipe
            await Task.Run(() => app.App.DisconnectAndWipeAsync());
            log.Check("the host was told to return to its first-run state", returned == 1);
            log.Check("the scheduler is stopped", !app.Scheduler.IsRunning);
            log.Check("no key blob and no secrets folder is left", !Directory.Exists(Path.Combine(root, "secrets")));
            log.Check("no state file is left", !File.Exists(Path.Combine(root, "state.json")));
            log.Check("no replica database is left", Directory.GetFiles(root, "beememorybank.db*").Length == 0);
            log.Check("no backups folder is left", !Directory.Exists(Path.Combine(root, "blind-backups")));
            await app.DisposeAsync();

            app = BlindDesktopRuntime.Create(platform, () => null, () => { }, scheduler: options, displayName: () => "E2E desktop");
            await app.StartAsync();
            var fresh = app.App.GetStatus();
            log.Check("the next start is a fresh first run (new identity, not paired)", fresh.NodeId is not null && !fresh.IsPaired && fresh.AwaitingAnswer && fresh.StartError is null);
            log.Say("RESULT: ALL PASS");
        }
        catch (Exception ex)
        {
            log.Say("RESULT: FAILED - " + ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        finally
        {
            if (app is not null) await app.DisposeAsync();
            nodeA?.Stop();
            nodeB?.Stop();
            var stopped = Shell.Run("docker", "stop", container);
            log.Say($"stopped container {container} (exit {stopped.ExitCode}); processes of the full nodes stopped");
            log.Say($"LEFT ON DISK (nothing was removed): container {container} (stopped), volumes {volumes[0]} and {volumes[1]}, folder {work}");
        }
    }

    private static int BlindArticleCount(string container)
    {
        var match = Regex.Match(Shell.Run("docker", "exec", container, "bmb", "blind", "status").Output, @"(\d+) articles");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static int ReplicaArticleCount(string dbPath)
    {
        if (!File.Exists(dbPath)) return -1;
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM tbl_article";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return -1; // being replaced by a replica switch
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The real Windows seams with a test autostart registration.</summary>
    private sealed class E2EPlatform(IBlindDesktopPlatform inner) : IBlindDesktopPlatform
    {
        public string Name => inner.Name;
        public IBlindPaths Paths => inner.Paths;
        public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
        public bool SignalRunningInstance() => inner.SignalRunningInstance();

        public void AddSeams(IServiceCollection services)
        {
            inner.AddSeams(services);
            services.AddSingleton<IBlindAutostart>(new FakeAutostart());
        }
    }

    private sealed class FileExporter(string folder) : IBlindBackupExporter
    {
        public Task<Stream> CreateAsync(string suggestedName, CancellationToken ct)
        {
            Directory.CreateDirectory(folder);
            return Task.FromResult<Stream>(new FileStream(Path.Combine(folder, suggestedName), FileMode.Create, FileAccess.Write, FileShare.None));
        }
    }

    // ---- infrastructure -----------------------------------------------------------------------------------------------------

    private sealed class E2ELog : IDisposable
    {
        private readonly StreamWriter _writer;
        public E2ELog(string path) => _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        public void Dispose() => _writer.Dispose();

        public void Say(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss} {message}";
            lock (_writer) _writer.WriteLine(line);
            Console.Error.WriteLine(line);
        }

        public void Step(string message) => Say("STEP " + message);

        public void Check(string name, bool ok, string? detail = null)
        {
            Say((ok ? "PASS " : "FAIL ") + name + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            ok.Should().BeTrue(name);
        }

        public async Task WaitFor(string what, TimeSpan timeout, Func<bool> condition, int pollSeconds = 3)
        {
            var end = DateTime.UtcNow + timeout;
            while (true)
            {
                if (condition()) { Say("PASS " + what); return; }
                if (DateTime.UtcNow > end) { Say("FAIL timeout waiting for " + what); throw new TimeoutException("timeout waiting for " + what); }
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
            }
        }
    }

    private static class Shell
    {
        public sealed record Result(int ExitCode, string Output, string Error);

        public static Result Run(string file, params string[] args)
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2))) { process.Kill(entireProcessTree: true); throw new TimeoutException(file + " " + string.Join(' ', args)); }
            return new Result(process.ExitCode, output.Result, error.Result);
        }
    }

    /// <summary>A real full node (the Api exe) with its own data folder and port, started hidden and stopped by the test.</summary>
    private sealed class FullNode
    {
        private Process? _process;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
        public string Url { get; private init; } = "";

        public static async Task<FullNode> StartAsync(string exe, string data, int port, E2ELog log, string name)
        {
            Directory.CreateDirectory(data);
            var node = new FullNode { Url = $"http://127.0.0.1:{port}" };
            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var (key, value) in new Dictionary<string, string>
                     {
                         ["BMB_DATA_PATH"] = data, ["ASPNETCORE_URLS"] = node.Url, ["BMB_INTERNAL_KEY"] = InternalKey,
                         ["ASPNETCORE_ENVIRONMENT"] = "Production", ["BMB_MDNS_ENABLED"] = "false", ["BMB_SYNC_INTERVAL_SECONDS"] = "5",
                     })
                start.Environment[key] = value;
            node._process = Process.Start(start)!;
            var nodeLog = new StreamWriter(Path.Combine(data, "..", $"full-node-{name}.log")) { AutoFlush = true };
            node._process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (nodeLog) nodeLog.WriteLine(e.Data); };
            node._process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (nodeLog) nodeLog.WriteLine(e.Data); };
            node._process.BeginOutputReadLine();
            node._process.BeginErrorReadLine();
            log.Say($"full node {name} started (pid {node._process.Id}) at {node.Url}");
            await log.WaitFor($"full node {name} answers /health", TimeSpan.FromMinutes(2),
                () => node.Send(HttpMethod.Get, "/health", null).Result.Status == HttpStatusCode.OK);
            return node;
        }

        public void Stop()
        {
            try
            {
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public async Task InitializeAsync()
        {
            (await Send(HttpMethod.Post, "/api/init/standalone", new { adminUsername = "admin", displayName = "E2E PC", password = Password })).Status
                .Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
            await UnlockAsync();
        }

        /// <summary>
        /// The owner signs in on the PC. The network builds its recovery box then - but only once a blind peer exists, so this comes AFTER the pairing
        /// with the blind node. Without that box no backup can be written (it would carry no way to open it) and "Back up now" waits.
        /// </summary>
        public async Task SignInAsync() =>
            (await Send(HttpMethod.Post, "/api/session/login", new { username = "admin", password = Password })).Status.Should().Be(HttpStatusCode.OK);

        public async Task UnlockAsync() =>
            (await Send(HttpMethod.Post, "/api/session/unlock", new { password = Password })).Status.Should().Be(HttpStatusCode.OK);

        public async Task WriteArticleAsync(string title, string content) =>
            (await Send(HttpMethod.Post, "/api/articles", new { title, treePath = "/e2e", content })).Status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        public async Task<Guid> PairBlindNodeAsync(string code)
        {
            var reply = await Send(HttpMethod.Post, "/api/blind-nodes", new { code });
            reply.Status.Should().Be(HttpStatusCode.OK, reply.Body);
            return Guid.Parse(JsonDocument.Parse(reply.Body).RootElement.GetProperty("nodeId").GetString()!);
        }

        /// <summary>The computer's side of pairing the app: its call code (what Android's page gets from the same route).</summary>
        public async Task<string?> PairPhoneAsync(string phoneCode, Guid listenerId)
        {
            var reply = await Send(HttpMethod.Post, "/api/blind-nodes/android", new { code = phoneCode, listenerId });
            reply.Status.Should().Be(HttpStatusCode.OK, reply.Body);
            return JsonDocument.Parse(reply.Body).RootElement.GetProperty("callCode").GetString();
        }

        public async Task RestoreFromBackupAsync(string path, string password)
        {
            var start = await Send(HttpMethod.Post, "/api/restore/backup",
                new { path, password, adminUsername = "admin", displayName = "E2E restored" });
            start.Status.Should().Be(HttpStatusCode.Accepted, start.Body);
            var end = DateTime.UtcNow.AddMinutes(6);
            while (DateTime.UtcNow < end)
            {
                var progress = await Send(HttpMethod.Get, "/api/restore/progress", null);
                var state = JsonDocument.Parse(progress.Body).RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state == "done") return;
                if (state is "failed" or "cancelled") throw new InvalidOperationException("restore " + state + ": " + progress.Body);
                await Task.Delay(2000);
            }
            throw new TimeoutException("the restore did not finish");
        }

        public async Task<List<string>> ArticleTitlesAsync()
        {
            var reply = await Send(HttpMethod.Get, "/api/articles", null);
            reply.Status.Should().Be(HttpStatusCode.OK, reply.Body);
            var titles = new List<string>();
            Collect(JsonDocument.Parse(reply.Body).RootElement, titles);
            return titles;

            static void Collect(JsonElement element, List<string> into)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) Collect(item, into);
                        break;
                    case JsonValueKind.Object:
                        if (element.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) into.Add(title.GetString()!);
                        foreach (var property in element.EnumerateObject())
                            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Collect(property.Value, into);
                        break;
                }
            }
        }

        private async Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object? body)
        {
            try
            {
                using var request = new HttpRequestMessage(method, Url + path);
                request.Headers.Add("X-Internal-Key", InternalKey);
                request.Headers.Add("X-User-Role", "superadmin");
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await _http.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (HttpRequestException ex)
            {
                return (0, ex.Message);
            }
        }
    }
}
