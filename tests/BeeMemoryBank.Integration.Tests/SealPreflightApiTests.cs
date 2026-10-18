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
/// The preflight of "Encrypt this node", the erase of unencrypted copies and the warnings, through the routes the page
/// calls (docs/design/step-b-spec.md 10.2, 11.2; red tests 15.3.8, 15.3.9, 15.3.17). There is no way-back snapshot since 2.6.2 (BMB-306).
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

    /// <summary>
    /// BMB-306: encrypting keeps no copy of the plain database, so the preflight says plainly, whatever else it says, that going back is not possible
    /// from the node (every client shows the warning), and a node that has been encrypted holds no pre-encryption folder and no way-back in its progress.
    /// </summary>
    [Fact]
    public async Task The_preflight_warns_that_going_back_is_not_possible_from_the_node_and_the_run_keeps_no_copy()
    {
        using var node = await StartAsync();
        using var client = node.Client();

        var preflight = await SealedApiNode.GetJsonAsync(client, "/api/vault/seal/preflight");
        var warning = preflight.GetProperty("warnings").EnumerateArray().Single(w => w.GetProperty("code").GetString() == "no_way_back");
        warning.GetProperty("message").GetString().Should().Contain("Going back is not possible from the node").And.Contain("no copy");
        preflight.GetProperty("can_start").GetBoolean().Should().BeTrue("a warning does not block");
        Codes(preflight, "blockers").Should().NotContain("snapshot_limit", "there is no snapshot to be too large for");

        using var start = await client.PostAsJsonAsync("/api/vault/seal", new { password = Password });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var progress = await WaitForEndAsync(client);
        progress.GetProperty("phase").GetString().Should().Be("done", progress.ToString());
        progress.TryGetProperty("snapshot", out _).Should().BeFalse("no way-back snapshot is made, so none is reported");
        Directory.Exists(Path.Combine(node.Dir, "pre-encryption")).Should().BeFalse("nothing was kept of the node as it was");
        Directory.EnumerateFiles(node.Dir, "bmb-snapshot-*", SearchOption.AllDirectories).Should().BeEmpty("not even a snapshot of any kind");
    }

    // ---------------------------------------------------------------- after the migration

    [Fact]
    public async Task After_the_migration_the_erase_works_on_the_sealed_node()
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
    }

    /// <summary>The routes of the way-back snapshot are gone (BMB-306): nothing lists it, nothing deletes it, nothing restores into the open from it.</summary>
    [Fact]
    public async Task The_way_back_routes_do_not_exist()
    {
        using var node = await StartAsync();
        using var client = node.Client();

        using (var get = await client.GetAsync("/api/vault/pre-encryption")) get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/vault/pre-encryption") { Content = JsonContent.Create(new { password = Password }) };
        using (var deleted = await client.SendAsync(delete)) deleted.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
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
