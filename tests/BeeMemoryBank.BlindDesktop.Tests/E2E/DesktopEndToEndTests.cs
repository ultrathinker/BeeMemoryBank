using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.BlindDesktop.Platform;
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
/// The whole life of the Windows blind app against real nodes, without a window: the account host composed with the real Windows adapters (DPAPI and the
/// atomic state file per account, the paths) in a scratch folder and driven by the real device loop; the listener is the Docker blind node image;
/// the "PC" is a real full node (Api exe). Steps, each logged with PASS or FAIL to the e2e log:
/// <list type="number">
/// <item>start the blind node container and full node A, pair them;</item>
/// <item>"Add account" makes an account with its identity and phone code; A pairs it (POST /api/blind-nodes/android); the account accepts the call code;</item>
/// <item>the device loop runs the first load; the replica holds A's articles;</item>
/// <item>an article written on A reaches the blind node and then the app by a sync round ("Sync now");</item>
/// <item>"Back up now" (repeated while the computer's sealed copy of the backup key has not arrived by sync) makes a backup;</item>
/// <item>"Save to..." copies it; a FRESH full node B restores it from that file with the master password and shows the articles;</item>
/// <item>a SECOND account on the same device (as a second device of the same mesh: a second mesh needs a second blind node and a third full node) has its own
/// identity, keys, folder and first load, and syncs in the same loop;</item>
/// <item>"Remove" takes the second account away with its keys and folder and leaves the first untouched; removing the first leaves no key, state or data, and the next
/// start is a fresh first run.</item>
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

            // 2. the app: no account on a new device; "Add account" the old way makes one with its identity and phone code; A pairs it; the app accepts the call code
            var root = Path.Combine(work, "desktop-app");
            var platform = new E2EPlatform(PlatformSelector.Create(root));
            var reports = new List<string>();
            var options = new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromSeconds(1) };
            app = BlindDesktopRuntime.Create(platform, () => null, scheduler: options,
                displayName: () => "E2E desktop", exporter: new FileExporter(Path.Combine(work, "saved")));
            app.Scheduler.UserJobReported += (id, said) => { lock (reports) reports.Add(said); log.Say($"user sees [{id[..Math.Min(8, id.Length)]}]: " + said); };
            await app.StartAsync();
            log.Check("a new device has no account", app.Accounts.GetSnapshot().Accounts.Count == 0);
            var added = await app.Accounts.AddAsync("E2E desktop");
            log.Check("Add account makes an account with a folder of its own", added.Ok && Directory.Exists(Path.Combine(root, "accounts", added.AccountId ?? "-")), added.Error);
            var accountId = added.AccountId!;
            var account = app.Accounts.For(accountId);
            var accountFolder = Path.Combine(root, "accounts", accountId);
            var phoneCode = account.PairingCode();
            log.Check("the account shows a phone code", phoneCode?.StartsWith("bmb-blind-phone:?") == true);
            var callCode = await nodeA.PairPhoneAsync(phoneCode!, blindNodeId);
            log.Check("PC pairs the account and returns a call code", callCode is not null);
            log.Check("the account accepts the call code", account.AcceptCallCode(callCode!) is null);
            app.Scheduler.RequestHeavy(accountId); // what the Connect button does

            // 3. the device loop runs the first load
            await log.WaitFor("first load", TimeSpan.FromMinutes(6), () =>
            {
                var s = account.GetStatus();
                log.Say($"  status: job={s.ActiveJob ?? "-"} progress={s.JobProgress?.ToString("P0") ?? "-"} loaded={s.InitialLoadDone}");
                return s.InitialLoadDone;
            }, pollSeconds: 2);
            var replica = Path.Combine(accountFolder, "beememorybank.db");
            log.Check("the replica holds the 3 articles", ReplicaArticleCount(replica) >= 3, "count=" + ReplicaArticleCount(replica));

            // 4. a later article reaches the app by sync
            await nodeA.WriteArticleAsync("desktop e2e after pairing", "late " + new string('y', 2000));
            await log.WaitFor("blind node holds the 4th article", TimeSpan.FromMinutes(3), () => BlindArticleCount(container) >= 4);
            var syncedBefore = account.GetStatus().LastSyncAt;
            app.Scheduler.RequestSync(accountId);
            await log.WaitFor("sync round brings the 4th article", TimeSpan.FromMinutes(2), () =>
                ReplicaArticleCount(replica) >= 4
                && account.GetStatus().LastSyncAt is { } at && (syncedBefore is null || at > syncedBefore), pollSeconds: 2);
            log.Check("a sync round ran and the replica holds 4 articles", ReplicaArticleCount(replica) >= 4);

            // 5. a backup. It needs the network's recovery box, an integrity anchor (a full node publishes the first one at the end of its first
            //    10-minute tick) and a package of the listener that includes that anchor (the listener renews its package every 30 minutes; the
            //    test restarts ITS OWN container instead of waiting for that). "Back up now" is asked again until a backup exists.
            var restartedAt = DateTime.MinValue;
            await log.WaitFor("a backup is made", TimeSpan.FromMinutes(30), () =>
            {
                if (account.GetStatus().Backups.Count > 0) return true;
                app.Scheduler.RequestSync(accountId);
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
                app.Scheduler.RequestBackup(accountId);
                return false;
            }, pollSeconds: 10);
            var backup = account.GetStatus().Backups.First();
            log.Check("a finished backup exists", backup.Size > 0, backup.Name + " " + backup.Size + " bytes");

            // 6. save it, restore it on a FRESH full node
            var copied = await account.ExportBackupAsync(backup.Name);
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

            // 6b. a SECOND account on the same device. A second mesh would need a second blind node container and a third full node, so this
            //     account joins the same mesh as a second device of the owner's: what the step proves is the device side - its own folder, identity,
            //     keys and state, its own first load by the same loop - while the isolation of two MESHES is proved in process by the tests with two
            //     hubs and their canaries (WindowsCompositionTests).
            var second = await app.Accounts.AddAsync("E2E second");
            log.Check("a second account is made", second.Ok, second.Error);
            var secondId = second.AccountId!;
            var secondAccount = app.Accounts.For(secondId);
            var secondFolder = Path.Combine(root, "accounts", secondId);
            log.Check("the two accounts have different identities", secondAccount.GetStatus().NodeId != account.GetStatus().NodeId);
            log.Check("the second account has its own DPAPI blobs", Directory.GetFiles(Path.Combine(secondFolder, "secrets"), "*.dpapi").Length == 3);
            log.Check("the second account's folder is its own", Directory.Exists(secondFolder) && secondFolder != accountFolder);
            var secondCallCode = await nodeA.PairPhoneAsync(secondAccount.PairingCode()!, blindNodeId);
            log.Check("PC pairs the second account", secondCallCode is not null);
            log.Check("the second account accepts its call code", secondAccount.AcceptCallCode(secondCallCode!) is null);
            app.Scheduler.RequestHeavy(secondId);
            await log.WaitFor("the second account's first load", TimeSpan.FromMinutes(6), () => secondAccount.GetStatus().InitialLoadDone, pollSeconds: 2);
            var secondReplica = Path.Combine(secondFolder, "beememorybank.db");
            log.Check("the second replica holds the 4 articles", ReplicaArticleCount(secondReplica) >= 4, "count=" + ReplicaArticleCount(secondReplica));
            var firstSyncAt = account.GetStatus().LastSyncAt;
            var secondSyncAt = secondAccount.GetStatus().LastSyncAt;
            app.Scheduler.RequestSync(accountId);
            app.Scheduler.RequestSync(secondId);
            await log.WaitFor("a sync round of each account", TimeSpan.FromMinutes(3), () =>
                account.GetStatus().LastSyncAt is { } a && (firstSyncAt is null || a > firstSyncAt)
                && secondAccount.GetStatus().LastSyncAt is { } b && (secondSyncAt is null || b > secondSyncAt), pollSeconds: 2);
            log.Check("the device lists both accounts", app.Accounts.GetSnapshot().Accounts.Count == 2);

            // 7. Remove: the second account goes with its keys and folder and the first is untouched; then the first goes and nothing is left
            await app.Accounts.RemoveAsync(secondId);
            log.Check("the second account's folder is gone", !Directory.Exists(secondFolder));
            log.Check("the first account is untouched (paired, keys, replica)",
                app.Accounts.For(accountId).GetStatus() is { IsPaired: true } && Directory.GetFiles(Path.Combine(accountFolder, "secrets"), "*.dpapi").Length >= 2 && ReplicaArticleCount(replica) >= 4);
            await app.Accounts.RemoveAsync(accountId);
            log.Check("the loop still runs (it is the device's, not the account's)", app.Scheduler.IsRunning);
            log.Check("no account folder is left", !Directory.Exists(accountFolder) && !Directory.Exists(secondFolder));
            log.Check("no secrets folder is left in the root", !Directory.Exists(Path.Combine(root, "secrets")));
            log.Check("no replica database is left", Directory.GetFiles(root, "beememorybank.db*", SearchOption.AllDirectories).Length == 0);
            log.Check("the list of accounts is empty", app.Accounts.GetSnapshot().Accounts.Count == 0);
            await app.DisposeAsync();

            app = BlindDesktopRuntime.Create(platform, () => null, scheduler: options, displayName: () => "E2E desktop");
            await app.StartAsync();
            log.Check("the next start is a fresh first run (no account, ready to add one)", app.Accounts.GetSnapshot() is { Accounts.Count: 0, CanAdd: true, RegistryProblem: null });
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
        public IBlindSecretStoreFactory SecretStores => inner.SecretStores;
        public IBlindStateStoreFactory StateStores => inner.StateStores;
        public IBlindDeviceConditions DeviceConditions => inner.DeviceConditions;
        public IBlindAutostart Autostart { get; } = new FakeAutostart();
        public IInstanceGuard? TryAcquireInstance() => inner.TryAcquireInstance();
        public bool SignalRunningInstance() => inner.SignalRunningInstance();
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
