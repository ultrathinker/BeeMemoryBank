using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Api of a sealed node, as the product's own <c>Program</c> runs it over a real sealed data folder (docs/design/step-b-spec.md 15.3.2, 4, 5, 6,
/// 16): it starts closed without touching the database, answers by the table of 5.6 while closed, opens for the superadmin's password and for no
/// other, closes with the lock, and refuses to open while it is held for a file copy.
/// </summary>
public sealed class SealedApiGateTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>
{
    private const string Wrong = "definitely not the password";

    // ---------------------------------------------------------------- 15.3.2 start

    [Fact]
    public async Task A_sealed_node_starts_closed_and_no_connection_is_made_before_a_door_opens()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();
        node.Services.GetRequiredService<IVaultKeySource>().Should().BeOfType<CountingKeySource>("the test sees every request for a page key");

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        var vault = await SealedApiNode.GetJsonAsync(client, "/api/vault/status");
        vault.GetProperty("mode").GetString().Should().Be("sealed");
        vault.GetProperty("state").GetString().Should().Be("Closed");
        var session = await SealedApiNode.GetJsonAsync(client, "/api/session/status");
        session.GetProperty("isUnlocked").GetBoolean().Should().BeFalse();
        session.GetProperty("sealed").GetBoolean().Should().BeTrue();
        (await node.LoginAsync(client, Wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        node.Gate.State.Should().Be(VaultState.Closed);
        node.Factory.Counting!.Requests.Should().Be(0, "no vault or chat connection exists before a door opens");
    }

    [Fact]
    public async Task The_status_of_a_closed_node_carries_no_path_size_count_or_key()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();

        var json = await client.GetStringAsync("/api/vault/status");

        json.Should().NotContain(node.Dir, "no path");
        json.Should().NotContain("beememorybank.db").And.NotContain("chat.db");
        json.Should().NotContain(Convert.ToBase64String(node.Files.DatabaseKey)).And.NotContain(Convert.ToHexString(node.Files.DatabaseKey));
        var status = JsonDocument.Parse(json).RootElement;
        status.TryGetProperty("sealed_at", out _).Should().BeTrue();
        status.TryGetProperty("migration", out var migration).Should().BeTrue();
        migration.ValueKind.Should().Be(JsonValueKind.Null, "nothing is pending");
    }

    [Fact]
    public async Task Hosted_services_make_no_connection_to_a_closed_node()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();

        // The first iterations of the loops start 3 s (mDNS), 5 s (sync) and 10 s (cleanup, remote accounts) after the start.
        await Task.Delay(TimeSpan.FromSeconds(12));

        node.Gate.State.Should().Be(VaultState.Closed);
        node.Factory.Counting!.Requests.Should().Be(0, "a loop waits for the gate at the top of its iteration");
    }

    [Fact]
    public async Task Every_hosted_loop_that_reads_the_vault_awaits_the_gate_and_every_other_one_is_named()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();

        // Spec 5.5: these wait; the rest of the hosted services of a full node do not read the vault database.
        HashSet<string> waits =
        [
            "SyncScheduler", "CleanupService", "PendingIndexProcessor", "PendingEmbeddingProcessor", "AuditLogPruningHostedService",
            "RemoteAccountSyncScheduler", "ChatHistoryBackfillProcessor", "RecoveryReconcileWatcher", "StateAnchorScheduler", "MdnsAnnouncer",
            "BaselineScheduler",
        ];
        var types = node.Services.GetServices<IHostedService>().Select(s => s.GetType()).Distinct().ToList();

        foreach (var type in types.Where(t => waits.Contains(t.Name)))
            AwaitsTheGate(type).Should().BeTrue($"{type.Name} must wait for a usable database at the top of its loop");

        var others = types.Where(t => !waits.Contains(t.Name)).Select(t => t.Name).OrderBy(n => n).ToList();
        others.Should().BeEquivalentTo(OtherHostedServices, "a new hosted service says what it does while the database is closed (spec 5.5)");
        types.Select(t => t.Name).Should().Contain(["SyncScheduler", "CleanupService", "PendingIndexProcessor"]);
    }

    /// <summary>
    /// The hosted services of a full node that wait for nothing: they read no vault or chat database (spec 5.5 table). The framework's own
    /// (<c>GenericWebHostService</c>, <c>IdleTrackingBackgroundService</c>), the files-only ones (<c>DownloadCleanupHostedService</c> removes expired
    /// download files; <c>BlindReplicaPackageCache</c> cleans its own folder; <c>BlindBackupScheduleService</c> reads settings.json and starts a job only on
    /// a node configured as a blind backup host, which a full node is not), <c>BlindAlarmStarter</c> (builds the alarm service in memory; its reads happen
    /// in request handlers, which the middleware holds back), and track 2's boot-store services (<c>RelayMaintenance</c> trims the inbox,
    /// <c>UnlockMaterialWatcher</c> mirrors the boot store while the database is open).
    /// </summary>
    private static readonly string[] OtherHostedServices =
    [
        "BlindAlarmStarter", "BlindBackupScheduleService", "BlindReplicaPackageCache", "DownloadCleanupHostedService", "GenericWebHostService",
        "IdleTrackingBackgroundService", "RelayMaintenance", "UnlockMaterialWatcher",
    ];

    private static bool AwaitsTheGate(Type type)
    {
        var execute = type.GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException(type.Name + " has no ExecuteAsync of its own");
        var machine = execute.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException(type.Name + ".ExecuteAsync is not async");
        var move = machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var il = move.GetMethodBody()!.GetILAsByteArray()!;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F)) continue; // call, callvirt
            try
            {
                var called = move.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), machine.IsGenericType ? machine.GetGenericArguments() : null, null);
                if (called?.Name is "WaitUsableAsync" or "WaitVaultUsableAsync") return true;
            }
            catch (Exception ex) when (ex is ArgumentException or BadImageFormatException) { }
        }
        return false;
    }

    // ---------------------------------------------------------------- 15.3.2 the table of routes

    [Fact]
    public async Task Every_route_of_the_golden_table_answers_503_vault_sealed_while_closed_except_the_allow_list_and_the_relay_set()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();

        var golden = File.ReadAllLines(GoldenPath()).Select(l => l.Split(" | ")[0]).Where(l => l.Length > 0).ToList();
        var live = node.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(m => m + " " + e.RoutePattern.RawText))
            .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        live.Should().BeEquivalentTo(golden, "the table the walk goes through is the routes the node serves (docs/full-node/ROUTES.golden.txt)");

        var relay = node.Services.GetRequiredService<ISealedRelay>();
        var served = new[]
        {
            "GET /health", "GET /version", "GET /api/version", "GET /api/session/status", "GET /api/vault/status", "GET /api/snapshots/restore/progress",
            "POST /api/session/unlock", "POST /api/session/login", "POST /api/session/recover-access",
        };
        var blocked = new List<string>();
        var wrongStatus = new List<string>();
        foreach (var route in golden)
        {
            var (method, pattern) = (route.Split(' ')[0], route[(route.IndexOf(' ') + 1)..]);
            var path = Concrete(pattern);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT" or "PATCH") request.Content = new StringContent(BodyOf(route), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var isVaultSealed = response.StatusCode == HttpStatusCode.ServiceUnavailable && text.Contains("\"vault_sealed\"", StringComparison.Ordinal);

            var open = served.Contains(route) || relay.Handles(new PathString(path), method) || path.StartsWith("/mcp", StringComparison.Ordinal);
            if (open && isVaultSealed) wrongStatus.Add($"{route} was refused as vault_sealed");
            if (!open && !isVaultSealed) blocked.Add($"{route} answered {(int)response.StatusCode}: {Trim(text)}");
        }

        wrongStatus.Should().BeEmpty("the allow-list and the relay set are served while closed");
        blocked.Should().BeEmpty("every other route answers 503 vault_sealed without touching the database");
        node.Gate.State.Should().Be(VaultState.Closed);
        node.Factory.Counting!.Requests.Should().Be(0, "not one of those requests opened a connection");
    }

    [Fact]
    public async Task A_tool_call_to_a_closed_node_gets_the_json_rpc_error()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"bee_list_articles","arguments":{}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer bee_" + new string('a', 40));
        request.Headers.Add("Accept", "application/json, text/event-stream");

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        text.Should().Contain("vault_sealed").And.Contain("\"jsonrpc\"");
        text.Should().Contain("\"id\":7");
        node.Gate.State.Should().Be(VaultState.Closed);
        node.Factory.Counting!.Requests.Should().Be(0);
    }

    // ---------------------------------------------------------------- 15.3.4 login

    [Fact]
    public async Task Login_on_a_sealed_node_gives_one_answer_to_every_refusal_and_opens_only_for_the_superadmins_password()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();

        // A second, ordinary user (the vault is open for a moment to make it, then locked again).
        (await node.LoginAsync(client, SealedNodeTemplate.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        node.Gate.State.Should().Be(VaultState.Open);
        (await client.PostAsJsonAsync("/api/users", new { username = "bob", password = "BobPass12345", displayName = "Bob", role = "user" })).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();
        node.Gate.State.Should().Be(VaultState.Closed);

        var attempts = new (string Label, string User, string Password)[]
        {
            ("wrong password", "admin", Wrong),
            ("an ordinary user's right password", "bob", "BobPass12345"),
            ("an unknown user", "nobody", Wrong),
            ("the right password for a wrong user name", "nobody", SealedNodeTemplate.Password),
            ("the right password for an ordinary user's name", "bob", SealedNodeTemplate.Password),
        };
        var answers = new List<string>();
        foreach (var (label, user, password) in attempts)
        {
            using var response = await node.LoginAsync(client, password, user);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, label);
            answers.Add(await response.Content.ReadAsStringAsync());
            node.Gate.State.Should().Be(VaultState.Closed, "{0} leaves the database closed", label);
            node.Session.IsUnlocked.Should().BeFalse(label);
        }
        answers.Distinct().Should().HaveCount(1, "no refusal tells the caller which part was wrong");

        (await node.LoginAsync(client, SealedNodeTemplate.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        node.Gate.State.Should().Be(VaultState.Open);
    }

    // ---------------------------------------------------------------- 15.3.5 lock closes

    [Fact]
    public async Task The_lock_closes_the_database_and_the_next_login_opens_it_again()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();
        (await node.LoginAsync(client, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();
        node.Factory.Counting!.Requests.Should().BeGreaterThan(0, "the unlock opened connections");
        var vault = node.Services.GetRequiredService<DbConnectionFactory>();
        var chat = node.Services.GetRequiredService<ChatDbConnectionFactory>();
        using (var c = vault.CreateConnection()) c.State.Should().Be(System.Data.ConnectionState.Open);

        (await client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        node.Gate.State.Should().Be(VaultState.Closed);
        node.Session.IsUnlocked.Should().BeFalse();
        var open = () => { using var c = vault.CreateConnection(); };
        open.Should().Throw<VaultSealedException>("the vault factory has no key any more");
        var openChat = () => { using var c = chat.CreateConnection(); };
        openChat.Should().Throw<VaultSealedException>("nor has the chat factory");
        var moved = Path.Combine(node.Dir, "beememorybank.db.moved");
        File.Move(Path.Combine(node.Dir, "beememorybank.db"), moved);
        File.Move(moved, Path.Combine(node.Dir, "beememorybank.db"));   // no pooled handle holds the file (Windows would refuse)
        using (var response = await client.GetAsync("/api/articles/00000000-0000-0000-0000-000000000001"))
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Contain("vault_sealed");
        }

        (await node.LoginAsync(client, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();
        node.Gate.State.Should().Be(VaultState.Open);
        using var reopened = vault.CreateConnection();
        reopened.State.Should().Be(System.Data.ConnectionState.Open);
    }

    // ---------------------------------------------------------------- 15.3.6 open steps

    [Fact]
    public async Task Per_process_sweeps_run_at_the_first_open_only_and_per_open_steps_at_every_open()
    {
        var steps = new List<string>();
        using var node = await SealedApiNode.StartAsync(template, services => services.AddSingleton<IVaultOpenStep>(new RecordingStep(steps)));
        using var client = node.Client();
        var staging = Path.Combine(node.Dir, "beememorybank.db.standalone-staging");
        File.WriteAllBytes(staging, [1, 2, 3, 4]);

        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        File.Exists(staging).Should().BeTrue("nothing sweeps before a door opens");
        steps.Should().BeEmpty();

        (await node.LoginAsync(client, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();
        File.Exists(staging).Should().BeFalse("the crash sweeps run at the first open of the process");
        steps.Should().Equal("first");

        (await client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();
        File.WriteAllBytes(staging, [1, 2, 3, 4]);
        (await node.LoginAsync(client, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();
        File.Exists(staging).Should().BeTrue("a second open of the same process sweeps nothing");
        steps.Should().Equal("first", "again");
    }

    private sealed class RecordingStep(List<string> log) : IVaultOpenStep
    {
        public int Order => 10_000;

        public Task RunAsync(bool firstOpenInProcess, CancellationToken ct)
        {
            log.Add(firstOpenInProcess ? "first" : "again");
            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- 15.3.16 hold

    [Fact]
    public async Task A_closed_node_held_for_a_file_copy_refuses_every_unlock_with_vault_busy_and_writes_nothing()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();
        var boot = Path.Combine(node.Dir, "beememorybank.boot.db");
        var before = Hashes(node.Dir);

        await using (await node.Gate.HoldClosedAsync("a file copy"))
        {
            node.Gate.IsHeld.Should().BeTrue();
            foreach (var response in new[]
            {
                await node.LoginAsync(client, SealedNodeTemplate.Password),
                await client.PostAsJsonAsync("/api/session/unlock", new { password = SealedNodeTemplate.Password }),
            })
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                (await response.Content.ReadAsStringAsync()).Should().Contain("vault_busy");
                response.Headers.RetryAfter.Should().NotBeNull();
            }
            node.Gate.State.Should().Be(VaultState.Closed);
            node.Session.IsUnlocked.Should().BeFalse();
            Hashes(node.Dir)[Path.GetFileName(boot)].Should().Be(before[Path.GetFileName(boot)], "a refused open writes nothing to the boot store");
        }

        node.Gate.IsHeld.Should().BeFalse();
        (await node.LoginAsync(client, SealedNodeTemplate.Password)).EnsureSuccessStatusCode();
        node.Gate.State.Should().Be(VaultState.Open);
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f =>
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(s));
            });

    private static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "full-node", "ROUTES.golden.txt"))) dir = dir.Parent;
        return Path.Combine((dir ?? throw new FileNotFoundException("docs/full-node/ROUTES.golden.txt")).FullName, "docs", "full-node", "ROUTES.golden.txt");
    }

    /// <summary>A path the router matches: guid, int and plain segments get a value of their kind.</summary>
    private static string Concrete(string pattern)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{\*{0,2}([A-Za-z0-9_]+)(:[a-z]+(\([^)]*\))?)?\??\}", m =>
        {
            var kind = m.Groups[2].Value;
            return kind switch
            {
                ":guid" => "00000000-0000-0000-0000-000000000001",
                ":int" or ":long" => "1",
                ":bool" => "true",
                _ => "x",
            };
        });
        return path.Length == 0 ? "/" : path;
    }

    /// <summary>The body of a route that reads one: a shape the handler would accept, so that an allowed route is judged by its own answer.</summary>
    private static string BodyOf(string route) => route switch
    {
        "POST /api/session/unlock" => $$"""{"password":"{{Wrong}}"}""",
        "POST /api/session/login" => $$"""{"username":"admin","password":"{{Wrong}}"}""",
        "POST /api/session/recover-access" => """{"username":"admin","recoveryKey":"wrong","newPassword":"AnotherPass12345"}""",
        _ => "{}",
    };

    private static string Trim(string text) => text.Length <= 120 ? text : text[..120] + "...";
}
