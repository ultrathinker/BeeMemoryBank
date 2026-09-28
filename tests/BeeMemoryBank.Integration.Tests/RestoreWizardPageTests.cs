using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>The Setup page's restore wizard (plan 6.7, 6.8), driven through the real Web host.</summary>
[Collection(RestoreScenarioCollection.Name)]
public sealed class RestoreWizardPageTests(RestoreSourceFixture source) : IAsyncLifetime
{
    private static readonly Regex TokenField = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"");
    private readonly RecoveryTestFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _browser = null!;

    public Task InitializeAsync()
    {
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        _browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _browser.Dispose();
        ((IDisposable)_web).Dispose();
        _api.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SetupOffersTheRestore()
    {
        var page = await (await _browser.GetAsync("/Setup")).Content.ReadAsStringAsync();

        page.Should().Contain("Restore from a blind node").And.Contain("/Setup?step=restore");
    }

    [Fact]
    public async Task OpenExistingProfile_RecognisesABlindBackup()
    {
        var resp = await PostFormAsync("Migrate", new() { ["sourcePath"] = source.BackupFolder });

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("step=restore").And.Contain("backup=");
    }

    [Fact]
    public async Task RestoreFromBackup_ShowsTheAnchorDate_AndConfirmation()
    {
        var start = await PostFormAsync("RestoreBackup", new()
        {
            ["backupPath"] = source.BackupFolder, ["password"] = RestoreSourceFixture.Password,
            ["adminUsername"] = "admin", ["displayName"] = "Restored PC"
        });
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        start.Headers.Location!.ToString().Should().Contain("step=restoring");

        var page = "";
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            page = await (await _browser.GetAsync("/Setup?step=restoring")).Content.ReadAsStringAsync();
            if (!page.Contains("http-equiv=\"refresh\"")) break;
            await Task.Delay(500);
        }

        page.Should().Contain("Confirmed as of").And.Contain("matches the integrity anchor made under your master key");
    }

    [Fact]
    public async Task ANewerChangeNextToATamper_IsShownAsPartlyConfirmed_WithTheUncheckedItemNamed()
    {
        var folder = Path.Combine(source.Work, "partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(source.BackupFolder))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        string victim;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "beememorybank.db")};Pooling=False"))
        {
            conn.Open();
            victim = (await Dapper.SqlMapper.ExecuteScalarAsync<string>(conn, "SELECT id FROM tbl_article WHERE title = 'Under key 1'"))!.ToLowerInvariant();
            var bucket = BeeMemoryBank.Sync.Recovery.StateDigest.BucketOf(new BeeMemoryBank.Sync.Recovery.StateDigestEntry("article", victim, 0, ""));
            var neighbour = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid().ToString())
                .First(id => BeeMemoryBank.Sync.Recovery.StateDigest.BucketOf(new BeeMemoryBank.Sync.Recovery.StateDigestEntry("tombstone", id, 0, "")) == bucket);
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "INSERT INTO tbl_tombstone (article_id, created_at, expires_at, lamport_ts, source_node_id) VALUES (@Id, '2026-09-01', '2026-10-01', 1000000, @Node)",
                new { Id = neighbour, Node = source.SourceNodeId.ToString() });
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE tbl_article SET title = 'Tampered' WHERE lower(id) = @victim", new { victim });
        }

        var page = await RestoreAndReadResultAsync(folder);

        page.Should().Contain("Partly confirmed").And.Contain("were not checked at all").And.Contain(victim);
        page.Should().NotContain("Confirmed as of");
    }

    private async Task<string> RestoreAndReadResultAsync(string folder)
    {
        (await PostFormAsync("RestoreBackup", new()
        {
            ["backupPath"] = folder, ["password"] = RestoreSourceFixture.Password, ["adminUsername"] = "admin", ["displayName"] = "PC"
        })).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var page = "";
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            page = await (await _browser.GetAsync("/Setup?step=restoring")).Content.ReadAsStringAsync();
            if (!page.Contains("http-equiv=\"refresh\"")) break;
            await Task.Delay(500);
        }
        return page;
    }

    [Fact]
    public async Task AlteredBackup_IsShownAsUnconfirmed()
    {
        var folder = Path.Combine(source.Work, "altered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(source.BackupFolder))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "beememorybank.db")};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tbl_article SET title = 'Altered' WHERE title = 'Under key 1'";
            cmd.ExecuteNonQuery();
        }

        await PostFormAsync("RestoreBackup", new()
        {
            ["backupPath"] = folder, ["password"] = RestoreSourceFixture.Password, ["adminUsername"] = "admin", ["displayName"] = "PC"
        });

        var page = "";
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            page = await (await _browser.GetAsync("/Setup?step=restoring")).Content.ReadAsStringAsync();
            if (!page.Contains("http-equiv=\"refresh\"")) break;
            await Task.Delay(500);
        }

        page.Should().Contain("Unconfirmed.").And.NotContain("Confirmed as of");
    }

    [Fact]
    public async Task WrongPassword_IsShown()
    {
        await PostFormAsync("RestoreBackup", new()
        {
            ["backupPath"] = source.BackupFolder, ["password"] = "WrongPassword9",
            ["adminUsername"] = "admin", ["displayName"] = "PC"
        });

        var page = "";
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            page = await (await _browser.GetAsync("/Setup?step=restoring")).Content.ReadAsStringAsync();
            if (!page.Contains("http-equiv=\"refresh\"")) break;
            await Task.Delay(500);
        }

        page.Should().Contain("opens none of the recovery boxes");
    }

    /// <summary>A copy of the fixture's backup with <paramref name="sql"/> applied to its database.</summary>
    private string BackupCopy(string sql)
    {
        var folder = Path.Combine(source.Work, "wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(source.BackupFolder))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "beememorybank.db")};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return folder;
    }

    private async Task<List<Guid>> ActivePeersAsync()
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IWhitelistRepository>().GetAllActiveAsync())
            .Select(w => w.NodeId).ToList();
    }

    private async Task<HttpResponseMessage> PostPairsAsync(HttpClient browser, string page, string handler, List<KeyValuePair<string, string>> fields,
        string? sessionCookie = null)
    {
        var form = await browser.GetAsync(page);
        var html = await form.Content.ReadAsStringAsync();
        fields.Add(new("__RequestVerificationToken", TokenField.Match(html).Groups[1].Value));
        var path = page.Split('?')[0];
        var post = new HttpRequestMessage(HttpMethod.Post, $"{path}?handler={handler}") { Content = new FormUrlEncodedContent(fields) };
        var cookies = form.Headers.TryGetValues("Set-Cookie", out var set) ? set.Select(c => c.Split(';')[0]).ToList() : [];
        if (sessionCookie != null) cookies.Add(sessionCookie);
        if (cookies.Count > 0) post.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        return await browser.SendAsync(post);
    }

    [Fact]
    public async Task NoAnchor_TheWizardConfirmsTheInactiveDevices_WithTheMasterPassword()
    {
        // A legitimate backup whose data no anchor matches: every device comes back inactive. The wizard is the
        // way out — nobody is signed in yet, the master password confirms them.
        var folder = BackupCopy("UPDATE tbl_article SET title = 'Altered' WHERE title = 'Under key 1'");
        var page = await RestoreAndReadResultAsync(folder);
        page.Should().Contain("Confirm the selected devices");
        (await ActivePeersAsync()).Should().BeEmpty();
        var devices = new[] { source.SourceNodeId, source.OldLaptopId, source.BlindNodeIdentity };

        var wrong = await PostPairsAsync(_browser, "/Setup?step=restoring", "RestoreConfirmPeers",
            [.. devices.Select(d => new KeyValuePair<string, string>("nodeIds", d.ToString())), new("password", "NotThePassword1")]);
        wrong.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ActivePeersAsync()).Should().BeEmpty("a wrong master password confirms nothing");

        var ok = await PostPairsAsync(_browser, "/Setup?step=restoring", "RestoreConfirmPeers",
            [.. devices.Select(d => new KeyValuePair<string, string>("nodeIds", d.ToString())), new("password", RestoreSourceFixture.Password)]);

        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ok.Headers.Location!.ToString().Should().Contain("confirmed=3");
        (await ActivePeersAsync()).Should().BeEquivalentTo(devices);
    }

    [Fact]
    public async Task Admin_ListsARestoredDeviceNoAnchorVouchedFor_AndConfirmsIt()
    {
        var phone = Guid.NewGuid();
        var folder = BackupCopy(
            $@"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
               VALUES ('{phone}', 'Kitchen tablet', randomblob(32), 'A', 0, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z')");
        await RestoreAndReadResultAsync(folder);
        (await ActivePeersAsync()).Should().NotContain(phone);
        var admin = await WebLogin.LoginAsync(_web, "admin", RestoreSourceFixture.Password);
        var session = admin.DefaultRequestHeaders.GetValues("Cookie").Single();

        var adminPage = await (await admin.GetAsync("/Admin")).Content.ReadAsStringAsync();
        adminPage.Should().Contain("Restored devices awaiting confirmation").And.Contain("Kitchen tablet");

        var confirm = await PostPairsAsync(admin, "/Admin", "ConfirmRestoredPeer", [new("nodeId", phone.ToString())], session);

        confirm.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await ActivePeersAsync()).Should().Contain(phone);
    }

    private async Task<HttpResponseMessage> PostFormAsync(string handler, Dictionary<string, string> fields)
    {
        var form = await _browser.GetAsync("/Setup?step=restore");
        var html = await form.Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"] = TokenField.Match(html).Groups[1].Value;
        var post = new HttpRequestMessage(HttpMethod.Post, $"/Setup?handler={handler}") { Content = new FormUrlEncodedContent(fields) };
        if (form.Headers.TryGetValues("Set-Cookie", out var cookies))
            post.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        return await _browser.SendAsync(post);
    }
}
