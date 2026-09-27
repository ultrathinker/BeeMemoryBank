using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindConsole;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// "Disconnect and wipe" (plan §9) and the console login behind it. The wipe's two safety
/// properties get their own tests: it requires BOTH confirmations (console password re-entry and
/// the node's own name), and it destroys only THIS node's data volume — the restic repository,
/// wherever it lives, must survive it untouched.
/// </summary>
public class BlindWipeEndpointsTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _factory = new();
    private readonly string _repoDir =
        Path.Combine(Path.GetTempPath(), "bmb_blind_wipe_repo_" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _factory.InitializeNodeAsync(displayName: "BlindWipeNode", password: "wipePw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        if (Directory.Exists(_repoDir))
            try { Directory.Delete(_repoDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task SetConsolePasswordAsync(HttpClient client) =>
        (await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

    [Fact]
    public async Task Wipe_RequiresNodeName_AndConsolePassword()
    {
        using var client = _factory.CreateClient();
        await SetConsolePasswordAsync(client);

        var wrongName = await client.PostAsJsonAsync("/api/blind/wipe",
            new { consolePassword = "console-pw-123", confirmNodeName = "some-other-node" });
        wrongName.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the typed name is the second, independent confirmation");
        (await wrongName.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("BlindWipeNode", "the refusal says what to type, not just no");

        var wrongPw = await client.PostAsJsonAsync("/api/blind/wipe",
            new { consolePassword = "wrong", confirmNodeName = "blindwipenode" });
        wrongPw.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the right name alone is not enough — the console password is re-verified");
    }

    [Theory]
    [InlineData("/api/blind/wipe", "console")]
    [InlineData("/api/blind/wipe/cli", "cli")]
    public async Task WipeAudit_NamesTheToolFromTheRoute(string route, string source)
    {
        using var client = _factory.CreateClient();
        await SetConsolePasswordAsync(client);
        (await client.PostAsJsonAsync(route, new { consolePassword = "console-pw-123", confirmNodeName = "BlindWipeNode" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await File.ReadAllTextAsync(Path.Combine(_factory.DataPath, "wipe-audit.log"))).Should().Contain(
            $"initiated_by={source}", "an audit review must tell a browser wipe from a break-glass CLI wipe");
    }

    [Fact]
    public async Task Wipe_ATableThatRefuses_RollsEverythingBack_AndFails()
    {
        var peerId = Guid.NewGuid();
        var whitelist = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = peerId,
        });
        // The injected failure: a table that will not be cleared (a lock, a trigger, a future
        // constraint all look the same to the wipe).
        using (var conn = _factory.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER trg_test_refuse_wipe BEFORE DELETE ON tbl_whitelist BEGIN SELECT RAISE(ABORT, 'injected'); END";
            cmd.ExecuteNonQuery();
        }
        using var client = _factory.CreateClient();
        await SetConsolePasswordAsync(client);
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoType = "folder", repoFolder = _repoDir }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var wipe = await client.PostAsJsonAsync("/api/blind/wipe",
            new { consolePassword = "console-pw-123", confirmNodeName = "BlindWipeNode" });

        wipe.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the wipe must not report success over a table it could not clear");
        (await wipe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Contain("nothing was wiped");
        (await _factory.Services.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().NotBeNull(
            "tables cleared before the refusing one are rolled back too");
        (await whitelist.GetByNodeIdAsync(peerId)).Should().NotBeNull();
        File.Exists(Path.Combine(_factory.DataPath, "blind", "settings.json")).Should().BeTrue("files go only after the database is clear");
        _factory.Services.GetRequiredService<BlindJobManager>().IsWiping.Should().BeFalse("a failed wipe leaves the node usable");
    }

    [Fact]
    public async Task SettingsSave_DuringTheWipe_IsRefused_AndWritesNothing()
    {
        using var client = _factory.CreateClient();
        var jobs = _factory.Services.GetRequiredService<BlindJobManager>();
        (await jobs.BeginWipeAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        try
        {
            (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoFolder = _repoDir }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            jobs.EndWipe();
        }
        File.Exists(Path.Combine(_factory.DataPath, "blind", "settings.json")).Should().BeFalse();

        var store = _factory.Services.GetRequiredService<BlindBackupSettingsStore>();
        using (store.CloseAndDelete())
        {
            var save = () => store.Save(new BlindBackupSettings());
            save.Should().Throw<SettingsClosedException>(
                "a save that slipped past the endpoint's check must not recreate the file the wipe just removed");
        }
        store.Save(new BlindBackupSettings()); // reopened after the wipe
    }

    [Fact]
    public async Task Wipe_ClearsNodeData_KeepsConsoleLogin_KeepsRepositoryUntouched()
    {
        // Something of the mesh: a whitelisted peer. Something on disk: media ciphertext, backup
        // settings pointing at a "repository", and a snapshot object inside it.
        var peerId = Guid.NewGuid();
        var whitelist = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = peerId,
        });
        Directory.CreateDirectory(Path.Combine(_repoDir, "data"));
        Directory.CreateDirectory(Path.Combine(_factory.DataPath, "media"));
        var keepFile = Path.Combine(_repoDir, "data", "keep-me");
        await File.WriteAllTextAsync(keepFile, "backup object");
        await File.WriteAllTextAsync(Path.Combine(_factory.DataPath, "media", "pic.enc"), "ciphertext");
        // What a torn save leaves: the temp files carry the same secrets as the real ones.
        Directory.CreateDirectory(Path.Combine(_factory.DataPath, "blind"));
        await File.WriteAllTextAsync(Path.Combine(_factory.DataPath, "blind", "settings.json.tmp"), "{\"ResticPassword\":\"pw\"}");
        await File.WriteAllTextAsync(Path.Combine(_factory.DataPath, "blind", "jobs.json.tmp"), "[]");
        var cacheFile = Path.Combine(_factory.DataPath, "blind", "restic-cache", "0a1b2c", "index", "ff00");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        await File.WriteAllTextAsync(cacheFile, "cached repository index");
        using (var client = _factory.CreateClient())
        {
            await SetConsolePasswordAsync(client);
            (await client.PutAsJsonAsync("/api/blind/backup/settings", new
            {
                repoType = "folder", repoFolder = _repoDir, resticPassword = "pw",
            })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "fast" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var wipeClient = _factory.CreateClient();
        var wipe = await wipeClient.PostAsJsonAsync("/api/blind/wipe",
            new { consolePassword = "console-pw-123", confirmNodeName = "blindwipenode" });
        wipe.StatusCode.Should().Be(HttpStatusCode.OK);

        // The node's own data is gone…
        (await _factory.Services.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
        (await whitelist.GetByNodeIdAsync(peerId)).Should().BeNull("the mesh's copy of the whitelist dies with the node");
        File.Exists(Path.Combine(_factory.DataPath, "media", "pic.enc")).Should().BeFalse();
        File.Exists(Path.Combine(_factory.DataPath, "blind", "settings.json")).Should().BeFalse(
            "the restic password this node held in the clear is erased with the node");
        Directory.GetFiles(Path.Combine(_factory.DataPath, "blind"), "*.tmp").Should().BeEmpty(
            "a torn save's settings.json.tmp holds the restic and S3 secrets in the clear");
        Directory.Exists(Path.Combine(_factory.DataPath, "blind", "restic-cache", "0a1b2c")).Should().BeFalse(
            "restic's cache lives in per-repository subdirectories; the wipe must not leave them");
        File.Exists(Path.Combine(_factory.DataPath, "blind", "cpu-mode")).Should().BeFalse(
            "a wiped node comes back in the default mode, not the old operator's");

        // …the console stays operable, and the repository is untouched.
        File.Exists(Path.Combine(_factory.DataPath, "blind", "console.json")).Should().BeTrue(
            "the console password is local, never mesh data — keeping it lets the operator re-pair without the CLI");
        File.Exists(keepFile).Should().BeTrue("the wipe must never reach into the backup repository");
    }
}

/// <summary>
/// Console login (plan §9): Argon2-verified password, five failures lock for 15 minutes —
/// counted from the persisted journal, so a restart does not reset the attacker's counter.
/// </summary>
public class BlindConsoleAuthTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "consoleAuthPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Login_BeforeInit_IsRefused()
    {
        using var client = _factory.CreateClient();
        var r = await client.PostAsJsonAsync("/api/blind/console/login", new { password = "whatever" });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Login_WrongPasswordFiveTimes_LocksOutEvenTheCorrectOne()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" });

        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/blind/console/login", new { password = "nope" });
            (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean().Should().BeFalse();
        }

        var sixth = await client.PostAsJsonAsync("/api/blind/console/login", new { password = "console-pw-123" });
        var json = await sixth.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("ok").GetBoolean().Should().BeFalse("the lock does not care that this attempt knows the password");
        json.GetProperty("locked").GetBoolean().Should().BeTrue();

        // The journal shows the lock — that is what the sign-in log is for.
        var logins = await (await client.GetAsync("/api/blind/console/logins")).Content.ReadFromJsonAsync<JsonElement>();
        logins.EnumerateArray().First().GetProperty("outcome").GetString().Should().Be("locked");
    }

    [Fact]
    public async Task Login_Journal_RecordsTheBrowserAddressTheConsoleForwards()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" });
        await client.PostAsJsonAsync("/api/blind/console/login", new { password = "nope", remote = "192.168.1.50" });

        var logins = await (await client.GetAsync("/api/blind/console/logins")).Content.ReadFromJsonAsync<JsonElement>();
        logins.EnumerateArray().First().GetProperty("remote").GetString().Should().Be("192.168.1.50",
            "the Api's own peer is always the console process; the journal must name the browser");
    }

    [Fact]
    public async Task Password_ChangeRequiresTheCurrentOne()
    {
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "first-pw-123" });

        (await client.PostAsJsonAsync("/api/blind/console/password",
            new { currentPassword = "wrong", newPassword = "second-pw-456" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync("/api/blind/console/password",
            new { currentPassword = "first-pw-123", newPassword = "second-pw-456" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var login = await client.PostAsJsonAsync("/api/blind/console/login", new { password = "second-pw-456" });
        (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ConsoleRoutes_RequireTheInternalKey()
    {
        using var anon = _factory.Server.CreateClient(); // no X-Internal-Key header
        // PublicSurface does not list /api/blind/console — a keyless caller must learn nothing.
        (await anon.PostAsJsonAsync("/api/blind/console/login", new { password = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anon.GetAsync("/api/blind/console/logins")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anon.PostAsJsonAsync("/api/blind/wipe", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
