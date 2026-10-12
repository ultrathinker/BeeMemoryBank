using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The preflight of "Encrypt this node", the erase of unencrypted copies, the way-back snapshot's routes and the warnings, through the routes the page
/// calls (docs/design/step-b-spec.md 10.2, 10.7, 11.2; red tests 15.3.8, 15.3.9, 15.3.17).
/// </summary>
public sealed class SealPreflightApiTests(ActivatedPlainTemplate template) : IClassFixture<ActivatedPlainTemplate>
{
    private const string Password = ActivatedPlainTemplate.Password;

    private async Task<SealedApiNode> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_seal_pre_" + Guid.NewGuid().ToString("N"));
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        var node = SealedApiNode.PlainOver(dir, configure);
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        return node;
    }

    // ---------------------------------------------------------------- plain copies and the erase

    [Fact]
    public async Task An_empty_node_lock_held_like_bmbd_does_not_block_preflight_or_sealing()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        using var running = new FileStream(Path.Combine(node.Dir, "node.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        using var proof = HeldLockProofs.Register(HeldLockProofs.FromHeldHandle(Path.Combine(node.Dir, "node.lock"), running.SafeFileHandle));

        (await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight")).GetProperty("can_start").GetBoolean().Should().BeTrue();
        using var start = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitForEndAsync(client)).GetProperty("phase").GetString().Should().Be("done");
        node.Gate.IsSealed.Should().BeTrue();
    }

    [Fact]
    public async Task Plain_copies_block_the_start_and_the_erase_removes_exactly_the_listed_files_after_which_the_migration_runs()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        var outside = Path.Combine(Path.GetTempPath(), "bmb_outside_" + Guid.NewGuid().ToString("N") + ".db");
        var planted = new[]
        {
            "updates/pre-update-x/beememorybank.db", "tmp/leftover.db", "vault.rekey-abc/beememorybank.db", "media/looks-like-a-blob.bin",
            "snapshots/plain-snap.tar.gz",
        };
        foreach (var rel in planted.Take(4)) PlantPlainDatabase(Path.Combine(node.Dir, rel));
        PlantPlainArchive(Path.Combine(node.Dir, planted[4]));
        PlantPlainDatabase(outside);
        File.WriteAllText(Path.Combine(node.Dir, "notes.txt"), "an unrelated file of the owner");
        // The live database is a WAL database of a running node: its bytes move when SQLite checkpoints, so "not a copy" is judged by the file
        // itself (the same file, still a database) and by the node still opening it.
        var liveBefore = BeeMemoryBank.AppPaths.FileIdentity.TryGet(Path.Combine(node.Dir, "beememorybank.db"));
        liveBefore.Should().NotBeNull();

        var preflight = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight");
        preflight.GetProperty("can_start").GetBoolean().Should().BeFalse();
        Codes(preflight, "blockers").Should().Contain("plain_copies");
        var found = preflight.GetProperty("plain_copies").EnumerateArray().Select(c => c.GetProperty("path").GetString()!.Replace('\\', '/')).ToList();
        found.Should().Contain(planted, "each planted copy is listed: a plain database in updates/, tmp/, a *.rekey-* folder, media/ under another name, and a plain archive in snapshots/");
        found.Should().NotContain("notes.txt");

        // A wrong password erases nothing.
        using (var wrong = await client.PostAsJsonAsync("/api/vault/seal/erase-plain-copies", new { password = "wrong", paths = planted }))
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        planted.Select(p => File.Exists(Path.Combine(node.Dir, p))).Should().AllBeEquivalentTo(true);

        // The ask names the copies, an unrelated file, a path outside the folder and the live database: only the copies go.
        var ask = planted.Concat(["notes.txt", "../" + Path.GetFileName(outside), "beememorybank.db"]).ToArray();
        using var erase = await client.PostAsJsonAsync("/api/vault/seal/erase-plain-copies", new { password = Password, paths = ask });
        erase.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await erase.Content.ReadFromJsonAsync<JsonElement>();
        answer.GetProperty("erased").EnumerateArray().Select(e => e.GetString()!.Replace('\\', '/')).Should().BeEquivalentTo(planted);
        answer.GetProperty("refused").EnumerateArray().Select(r => r.GetProperty("path").GetString()).Should()
            .BeEquivalentTo(["notes.txt", "../" + Path.GetFileName(outside), "beememorybank.db"]);
        planted.Select(p => File.Exists(Path.Combine(node.Dir, p))).Should().AllBeEquivalentTo(false);
        File.Exists(Path.Combine(node.Dir, "notes.txt")).Should().BeTrue("an unrelated file stays");
        File.Exists(outside).Should().BeTrue("a path outside the data folder is refused");
        var liveAfter = BeeMemoryBank.AppPaths.FileIdentity.TryGet(Path.Combine(node.Dir, "beememorybank.db"));
        liveAfter!.Value.IsSameFileAs(liveBefore!.Value).Should().BeTrue("the live database is not a copy: it was not replaced or erased");
        liveAfter.Value.LinkCount.Should().Be(1u);
        (await SealedApiNode.GetJsonAsync(client, "/api/vault/status")).GetProperty("mode").GetString().Should().Be("plain");

        // The audit entry has a count and no names.
        using (var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            var detail = c.ExecuteScalar<string>("SELECT details FROM tbl_audit_log WHERE action = 'plain_copies_erased'");
            detail.Should().Contain("5").And.NotContain("tmp").And.NotContain("leftover");
        }

        // Nothing blocks any more: the migration runs through the routes the page uses.
        (await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight")).GetProperty("can_start").GetBoolean().Should().BeTrue();
        using var start = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var progress = await WaitForEndAsync(client);
        progress.GetProperty("phase").GetString().Should().Be("done", progress.ToString());
        node.Gate.IsSealed.Should().BeTrue();
        File.Delete(outside);
    }

    [Fact]
    public async Task A_second_name_for_the_live_database_blocks_as_shares_identity_and_the_erase_refuses_it_and_leaves_the_database_alone()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        var live = Path.Combine(node.Dir, "beememorybank.db");
        Directory.CreateDirectory(Path.Combine(node.Dir, "tmp"));
        TestLinks.CreateHardLink(live, Path.Combine(node.Dir, "tmp", "hidden-name.bin"));
        var liveBefore = ReadLiveDatabase(live);

        var preflight = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight");
        preflight.GetProperty("can_start").GetBoolean().Should().BeFalse();
        var kinds = preflight.GetProperty("plain_copies").EnumerateArray().Select(c => c.GetProperty("kind").GetString()).ToList();
        kinds.Should().Contain(PlainCopyKinds.SharesIdentity);

        using var erase = await client.PostAsJsonAsync("/api/vault/seal/erase-plain-copies", new { password = Password, paths = new[] { "tmp/hidden-name.bin", "beememorybank.db" } });
        var answer = await erase.Content.ReadFromJsonAsync<JsonElement>();
        answer.GetProperty("erased").GetArrayLength().Should().Be(0);
        answer.GetProperty("refused").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Contain(PlainCopyKinds.SharesIdentity);
        ReadLiveDatabase(live).Should().Equal(liveBefore, "the live database is byte-identical");
        using var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_user").Should().BeGreaterThan(0, "the node still opens");
    }

    [Fact]
    public async Task A_database_above_the_snapshot_limit_blocks_until_the_owner_ticks_the_box_and_then_runs_without_the_way_back_snapshot()
    {
        using var node = await StartAsync(services =>
        {
            // The product's own host, built the way the product builds it, behind a wrapper that finds the database too large.
            var original = services.Single(d => d.ServiceType == typeof(ISealMigrationHost)).ImplementationFactory!;
            services.Replace(ServiceDescriptor.Singleton<ISealMigrationHost>(sp => new LimitedHost((ISealMigrationHost)original(sp))));
        });
        using var client = node.Client();

        var blocked = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight");
        Codes(blocked, "blockers").Should().Contain("snapshot_limit");
        using (var refused = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password }))
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var ticked = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight?accept_no_snapshot=true");
        ticked.GetProperty("can_start").GetBoolean().Should().BeTrue();
        using var start = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password, accept_no_snapshot = true });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var progress = await WaitForEndAsync(client);
        progress.GetProperty("phase").GetString().Should().Be("done", progress.ToString());
        progress.GetProperty("snapshot").ValueKind.Should().Be(JsonValueKind.Null, "step 3 was skipped");
        Directory.Exists(Path.Combine(node.Dir, "pre-encryption")).Should().BeFalse();
    }

    /// <summary>The host of the Api with a database "too large" for the way-back snapshot; making the snapshot must not be attempted.</summary>
    private sealed class LimitedHost(ISealMigrationHost inner) : ISealMigrationHost
    {
        public bool CanMakeWayBackSnapshot => true;
        public bool FitsWayBackSnapshot(long databaseBytes) => false;
        public Task<WayBackSnapshot> CreateWayBackSnapshotAsync(string masterPassword, CancellationToken ct) =>
            throw new InvalidOperationException("step 3 must be skipped");
        public Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct) => inner.HostBlockersAsync(options, ct);
        public Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct) => inner.HostWarningsAsync(ct);
    }

    // ---------------------------------------------------------------- after the migration

    [Fact]
    public async Task After_the_migration_the_erase_works_on_the_sealed_node_and_the_way_back_snapshot_is_listed_and_deleted_on_the_owners_word()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        using (var start = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password })) start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitForEndAsync(client)).GetProperty("phase").GetString().Should().Be("done");

        // Mode Sealed: a copy planted later is found and erased.
        PlantPlainDatabase(Path.Combine(node.Dir, "tmp", "late-copy.db"));
        using (var erase = await client.PostAsJsonAsync("/api/vault/seal/erase-plain-copies", new { password = Password, paths = new[] { "tmp/late-copy.db" } }))
            (await erase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("erased").GetArrayLength().Should().Be(1);
        File.Exists(Path.Combine(node.Dir, "tmp", "late-copy.db")).Should().BeFalse();

        // The way back: shown to a signed-in superadmin by name, size and date, never in the unauthenticated status.
        var listed = await SealedApiNode.GetJsonAsync(client, "/api/vault/pre-encryption");
        listed.GetProperty("exists").GetBoolean().Should().BeTrue();
        var name = listed.GetProperty("name").GetString()!;
        (await client.GetStringAsync("/api/vault/status")).Should().NotContain(name);
        var snapshot = Path.Combine(node.Dir, "pre-encryption", name);
        File.WriteAllText(snapshot + ".sig", "a signature");
        File.WriteAllText(Path.Combine(node.Dir, "pre-encryption", "notes.txt"), "an unrelated file");

        // Only the owner, with the password, deletes it.
        using (var anonymous = node.Factory.CreateDefaultClient())
            (await anonymous.DeleteAsync("/api/vault/pre-encryption")).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);   // a route outside the public surface does not exist for a caller without the internal key
        using (var ordinaryUser = node.Client())
        {
            ordinaryUser.DefaultRequestHeaders.Remove("X-User-Role");
            ordinaryUser.DefaultRequestHeaders.Add("X-User-Role", "user");
            using var forbidden = await Delete(ordinaryUser, Password);
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        using (var wrong = await Delete(client, "wrong password")) wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        File.Exists(snapshot).Should().BeTrue();

        using (var deleted = await Delete(client, Password)) deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(snapshot).Should().BeFalse();
        File.Exists(snapshot + ".sig").Should().BeFalse("the signature goes with the snapshot");
        File.Exists(Path.Combine(node.Dir, "pre-encryption", "notes.txt")).Should().BeTrue("nothing else in the folder is the product's to delete");
        (await SealedApiNode.GetJsonAsync(client, "/api/vault/pre-encryption")).GetProperty("exists").GetBoolean().Should().BeFalse();
        using var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'pre_encryption_deleted'").Should().Be(1);
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/vault/pre-encryption") { Content = JsonContent.Create(new { password }) };
        return client.SendAsync(request);
    }

    // ---------------------------------------------------------------- warnings

    [Fact]
    public async Task A_node_that_starts_unlocked_after_a_restart_gets_the_auto_unlock_warning_and_keeps_its_choice()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        using (var enable = await client.PostAsJsonAsync("/api/keys/server-auto-unlock/enable", new { password = Password, confirmation = "START UNLOCKED" }))
            enable.StatusCode.Should().Be(HttpStatusCode.OK);

        var preflight = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight");
        Codes(preflight, "warnings").Should().Contain("auto_unlock_on");
        preflight.GetProperty("can_start").GetBoolean().Should().BeTrue("a warning does not block");

        using var start = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitForEndAsync(client)).GetProperty("phase").GetString().Should().Be("done");
        var status = await SealedApiNode.GetJsonAsync(client, "/api/vault/status");
        status.GetProperty("auto_unlock").GetProperty("server").GetBoolean().Should().BeTrue("the owner's choice is kept");
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string> Codes(JsonElement body, string list) =>
        body.GetProperty(list).EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToList();

    /// <summary>Reads the live SQLite database without requiring an exclusive Windows share.</summary>
    private static byte[] ReadLiveDatabase(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static async Task<JsonElement> WaitForEndAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/progress");
            if (progress.GetProperty("phase").GetString() is "done" or "failed") return progress;
            await Task.Delay(200);
        }
        throw new TimeoutException("The migration did not end in three minutes.");
    }

    private static void PlantPlainDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Execute("CREATE TABLE copy_of_something (id INTEGER PRIMARY KEY, body TEXT); INSERT INTO copy_of_something (body) VALUES ('a plain copy')");
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>A snapshot someone left in <c>snapshots/</c> with <c>dbEncrypted: false</c>: a tar.gz whose database entry is a plain SQLite file.</summary>
    private static void PlantPlainArchive(string path)
    {
        var db = path + ".db.tmp";
        PlantPlainDatabase(db);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip))
        {
            tar.WriteEntry(db, "beememorybank.db");
            var manifest = Path.Combine(Path.GetDirectoryName(path)!, "manifest.tmp");
            File.WriteAllText(manifest, """{"dbEncrypted": false}""", Encoding.UTF8);
            tar.WriteEntry(manifest, "manifest.json");
            File.Delete(manifest);
        }
        File.Delete(db);
    }
}
