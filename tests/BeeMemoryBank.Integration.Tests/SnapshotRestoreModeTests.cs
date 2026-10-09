using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The server decides which restore a caller gets, and writes down which one ran. The Admin dialog's
/// radio buttons are not the gate: a mode this route does not serve, a mode nobody defined, a caller
/// that is not a superadmin, an agent, or a wrong master password are each refused here — and a
/// refused request changes nothing.
///
/// The three modes: <c>standalone</c> (this node only, becomes a new node), <c>keep-identity</c> (the
/// snapshot replaces the database as it is; what an older client asked for with
/// <c>standaloneMode: false</c>), and <c>network</c> (its own route).
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class SnapshotRestoreModeTests : IAsyncLifetime
{
    private const string Password = "restoreModePassword";

    private readonly BmbWebApplicationFactory _node = new();
    private HttpClient _client = null!;
    private int _adminUserId;
    private int _plainUserId;

    public async Task InitializeAsync()
    {
        _client = _node.CreateClient();
        await _node.InitializeNodeAsync(displayName: "ModeNode", password: Password);
        await UnlockAsync();

        using var scope = _node.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _adminUserId = (await users.GetByUsernameAsync("admin"))!.Id;
        _plainUserId = await users.CreateAsync(new User
        {
            Username = "colleague", DisplayName = "Colleague", Role = UserRoles.User, IsActive = true, CreatedAt = DateTime.UtcNow
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    // ─── What the request may ask for ─────────────────────────────────────

    [Theory]
    [InlineData("network", "restore-network")]   // served by its own route, with its own checks
    [InlineData("everything", "Unknown restore mode")]
    [InlineData("STANDALONE ", null)]            // case and padding are forgiven, not a refusal
    public async Task TheModeOfARestoreRequest_IsCheckedByTheServer(string mode, string? refusalMentions)
    {
        var fileName = await CreateSnapshotAsync();
        var before = await IdentityAsync();

        var resp = await PostRestoreAsync(_client, new { fileName, masterPassword = Password, createBackupFirst = false, mode });

        if (refusalMentions is null)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return;
        }
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain(refusalMentions);
        (await IdentityAsync()).NodeId.Should().Be(before.NodeId, "a refused request changes nothing");
        _node.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeTrue(
            "it is refused before the password is looked at, so the session is as it was");
        var refusal = (await AuditAsync()).Where(a => a.Action == "snapshot_restore_refused").ToList();
        refusal.Should().ContainSingle("the refusal is on the record").Which.Details.Should().Contain($"Mode={mode}");
    }

    /// <summary>Review revsrv F10: the restore extracts the whole backup database (key slots, password hashes) while it works.</summary>
    [Fact]
    public async Task ARestore_WorksInTheDataFoldersStagingFolder_NotTheOsTempFolder_AndLeavesNothingThere()
    {
        var fileName = await CreateSnapshotAsync();

        var resp = await PostRestoreAsync(_client, new { fileName, masterPassword = Password, createBackupFirst = false, mode = "keep-identity" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var staging = BeeMemoryBank.Core.IO.SnapshotStaging.DirIn(_node.DataPath);
        Directory.Exists(staging).Should().BeTrue("the extraction folder is made there");
        Directory.GetFileSystemEntries(staging).Should().BeEmpty("the extracted database is removed once the restore is done");
    }

    [Fact]
    public async Task AStandaloneRestore_IsNamedInTheAuditLog_AndSaysWhichNodeItBecame()
    {
        var fileName = await CreateSnapshotAsync();

        var resp = await PostRestoreAsync(_client, new { fileName, masterPassword = Password, createBackupFirst = false, mode = "standalone" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mode").GetString().Should().Be("standalone");
        var identity = await IdentityAsync();
        body.GetProperty("newNodeId").GetGuid().Should().Be(identity.NodeId);
        body.GetProperty("nodeName").GetString().Should().Be("ModeNode");

        // The restored database is the one on disk now: it carries the entry written after the swap. The
        // "started" entry, written before it, lives on in the pre-restore snapshot — see the endpoint.
        var audit = await AuditAsync();
        audit.Should().Contain(a => a.Action == "snapshot_restored"
            && a.Details.Contains("Mode=standalone") && a.Details.Contains(identity.NodeId.ToString()));
    }

    [Fact]
    public async Task TheStartedEntry_NamesTheModeAndSurvivesInThePreRestoreBackup()
    {
        var fileName = await CreateSnapshotAsync();

        var resp = await PostRestoreAsync(_client, new { fileName, masterPassword = Password, createBackupFirst = true, mode = "standalone" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var backup = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("backupFileName").GetString()!;

        // Open the safety backup the way a later restore would (the restore locked the vault), and read what it recorded.
        await UnlockAsync();
        var dir = _node.Services.GetRequiredService<SnapshotService>().SnapshotsDir;
        var details = await ReadAuditOfArchiveAsync(Path.Combine(dir, backup), "snapshot_restore_started");
        details.Should().ContainSingle().Which.Should().Contain("Mode=standalone").And.Contain("backupFirst=True");
    }

    [Fact]
    public async Task AnOlderClientThatSaysStandaloneFalse_GetsTheKeepIdentityMode_AndKeepsItsIdentity()
    {
        var fileName = await CreateSnapshotAsync();
        var before = await IdentityAsync();

        var resp = await PostRestoreAsync(_client, new { fileName, masterPassword = Password, createBackupFirst = false, standaloneMode = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mode").GetString().Should().Be("keep-identity");
        body.GetProperty("newNodeId").ValueKind.Should().Be(JsonValueKind.Null);
        (await IdentityAsync()).NodeId.Should().Be(before.NodeId, "this mode restores the snapshot's own identity, which is this node's");
        (await AuditAsync()).Should().Contain(a => a.Action == "snapshot_restored" && a.Details.Contains("Mode=keep-identity"));
    }

    [Fact]
    public async Task AnOlderClientThatSaysStandaloneTrue_GetsTheStandaloneMode()
    {
        var fileName = await CreateSnapshotAsync();
        var before = await IdentityAsync();

        var resp = await PostRestoreAsync(_client, new { fileName, masterPassword = Password, createBackupFirst = false, standaloneMode = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString().Should().Be("standalone");
        (await IdentityAsync()).NodeId.Should().NotBe(before.NodeId);
    }

    // ─── Who may ask ──────────────────────────────────────────────────────

    [Fact]
    public async Task ARegularUser_CannotRestoreEitherWay()
    {
        var fileName = await CreateSnapshotAsync();
        var before = await IdentityAsync();
        using var user = ClientAs(_plainUserId, UserRoles.User);

        var local = await PostRestoreAsync(user, new { fileName, masterPassword = Password, createBackupFirst = false, mode = "standalone" });
        var network = await PostNetworkRestoreAsync(user, new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide", masterPassword = Password });

        local.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        network.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await IdentityAsync()).NodeId.Should().Be(before.NodeId);
        _node.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task AnAgentKey_EvenOneOwnedByASuperadmin_CannotRestoreEitherWay()
    {
        var fileName = await CreateSnapshotAsync();
        var before = await IdentityAsync();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        using (var scope = _node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "claude-desktop", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = _adminUserId
            });
        using var agent = _node.Server.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var local = await PostRestoreAsync(agent, new { fileName, masterPassword = Password, createBackupFirst = false, mode = "standalone" });
        var network = await PostNetworkRestoreAsync(agent, new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide", masterPassword = Password });

        local.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        network.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await IdentityAsync()).NodeId.Should().Be(before.NodeId);
    }

    [Fact]
    public async Task AWrongMasterPassword_IsRefusedForEveryMode_AndChangesNothing()
    {
        var fileName = await CreateSnapshotAsync();
        var before = await IdentityAsync();

        var standalone = await PostRestoreAsync(_client, new { fileName, masterPassword = "not-the-password", createBackupFirst = false, mode = "standalone" });
        var network = await PostNetworkRestoreAsync(_client, new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide", masterPassword = "not-the-password" });

        standalone.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        network.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await network.Content.ReadAsStringAsync()).Should().Contain("Invalid master password");
        (await IdentityAsync()).NodeId.Should().Be(before.NodeId);
        Directory.Exists(Path.Combine(SnapshotsDir, "restore-pending")).Should().BeFalse("nothing was prepared for the peers");
        var refusals = (await AuditAsync()).Where(a => a.Action == "snapshot_restore_refused").Select(a => a.Details).ToList();
        refusals.Should().Contain(d => d.Contains("Mode=network") && d.Contains("wrong master password"));
        refusals.Should().Contain(d => d.Contains("Mode=standalone") && d.Contains("wrong master password"));
        refusals.Should().NotContain(d => d.Contains("not-the-password"), "the password is never written down");
    }

    // ─── The whole network ────────────────────────────────────────────────

    [Fact]
    public async Task ANetworkRestore_OfASnapshotThisNodeMade_IsFoundByItsFileName_AndKeepsTheIdentity()
    {
        // This node's own snapshots carry no id in their names (bmb-snapshot-<time>.tar.gz), so the id the
        // route used to require could never find them: the Admin dialog's second option had nothing to start.
        await CreateArticleAsync("in-the-snapshot");
        var fileName = await CreateSnapshotAsync();
        await CreateArticleAsync("written-after-the-snapshot");
        var before = await IdentityAsync();

        // Exactly what `bmb snapshot restore-network <file name>` sends.
        var resp = await PostNetworkRestoreAsync(_client, new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide" });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString().Should().Be("network");
        await WaitForNetworkRestoreAsync();

        await UnlockAsync();
        (await IdentityAsync()).NodeId.Should().Be(before.NodeId, "the originator of a network restore stays the same node");
        var titles = await ArticleTitlesAsync();
        titles.Should().Contain("in-the-snapshot");
        titles.Should().NotContain("written-after-the-snapshot");
        (await AuditAsync()).Should().Contain(a => a.Action == "snapshot_restore_started" && a.Details.Contains("Mode=network"));
    }

    [Fact]
    public async Task ANetworkRestore_WithTheMasterPassword_StartsAsWell()
    {
        var fileName = await CreateSnapshotAsync();

        var resp = await PostNetworkRestoreAsync(_client, new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide", masterPassword = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted, await resp.Content.ReadAsStringAsync());
        await WaitForNetworkRestoreAsync();
    }

    [Theory]
    [InlineData("../beememorybank.db")]
    [InlineData("no-such-snapshot.tar.gz")]
    [InlineData("notes.txt")]
    public async Task ANetworkRestore_OfAFileThatIsNotASnapshotOfThisNode_IsNotFound(string fileName)
    {
        var resp = await PostNetworkRestoreAsync(_client, new { snapshotFileId = Guid.Empty, fileName, mode = "NetworkWide" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheNumericModeTheCliUsedToSend_IsTheStandaloneOne_AndIsRefused()
    {
        // `bmb snapshot restore-network` posted mode = 1, which is RestoreMode.Standalone: every call was
        // answered "Use /restore for standalone". It sends "NetworkWide" now; this pins what 1 means.
        var fileName = await CreateSnapshotAsync();

        var resp = await PostNetworkRestoreAsync(_client, new { snapshotFileId = Guid.Empty, fileName, mode = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private string SnapshotsDir => _node.Services.GetRequiredService<SnapshotService>().SnapshotsDir;

    private async Task UnlockAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private HttpClient ClientAs(int userId, string role)
    {
        var client = _node.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", role);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    private async Task<string> CreateSnapshotAsync()
    {
        var resp = await _client.PostAsync("/api/snapshots", null);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;
    }

    private static Task<HttpResponseMessage> PostRestoreAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync("/api/snapshots/restore", body);

    private static Task<HttpResponseMessage> PostNetworkRestoreAsync(HttpClient client, object body) =>
        client.PostAsync("/api/snapshots/restore-network",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    private async Task<NodeIdentity> IdentityAsync()
    {
        using var scope = _node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    private async Task CreateArticleAsync(string title)
    {
        var resp = await _client.PostAsJsonAsync("/api/articles", new { title, content = "body", treePath = "/" });
        resp.EnsureSuccessStatusCode();
    }

    private async Task<List<string>> ArticleTitlesAsync()
    {
        var resp = await _client.GetAsync("/api/articles");
        resp.EnsureSuccessStatusCode();
        var articles = await resp.Content.ReadFromJsonAsync<JsonElement[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return articles!.Select(a => a.GetProperty("title").GetString()!).ToList();
    }

    /// <summary>The network restore runs in the background; the progress route is how the page learns it is over.</summary>
    private async Task WaitForNetworkRestoreAsync()
    {
        for (var i = 0; i < 200; i++)
        {
            var progress = await (await _client.GetAsync("/api/snapshots/restore/progress")).Content.ReadFromJsonAsync<JsonElement>();
            var step = progress.GetProperty("currentStep").ToString();
            if (step is "Completed" or "Failed")
            {
                step.Should().Be("Completed", progress.ToString());
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("the network restore did not finish");
    }

    private record AuditRow(string Action, string Details);

    private async Task<List<AuditRow>> AuditAsync()
    {
        using var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<AuditRow>("SELECT action AS Action, details AS Details FROM tbl_audit_log ORDER BY id")).ToList();
    }

    /// <summary>The details of the audit rows named <paramref name="action"/> inside a snapshot archive (a database read from the bytes on disk).</summary>
    private async Task<List<string>> ReadAuditOfArchiveAsync(string snapshotPath, string action)
    {
        var extracted = Path.Combine(Path.GetTempPath(), $"bmb-audit-{Guid.NewGuid():N}.db");
        try
        {
            await using (var file = File.OpenRead(snapshotPath))
            await using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var tar = new System.Formats.Tar.TarReader(gz))
            {
                while (await tar.GetNextEntryAsync() is { } entry)
                {
                    if (!entry.Name.EndsWith("beememorybank.db", StringComparison.OrdinalIgnoreCase)) continue;
                    await using var outFile = File.Create(extracted);
                    await entry.DataStream!.CopyToAsync(outFile);
                    break;
                }
            }
            await _node.Services.GetRequiredService<SnapshotService>().DecryptDbIfNeededAsync(extracted);
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={extracted};Pooling=False");
            conn.Open();
            return (await conn.QueryAsync<string>("SELECT details FROM tbl_audit_log WHERE action = @action", new { action })).ToList();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(extracted)) File.Delete(extracted);
        }
    }
}
