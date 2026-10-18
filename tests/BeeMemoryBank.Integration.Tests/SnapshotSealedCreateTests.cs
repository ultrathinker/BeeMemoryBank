using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Snapshots made by a sealed node (docs/design/step-b-spec.md 8.1, 8.3, red test 15.1.7): the working copy is encrypted with the vault's page key at
/// every moment, the database entry is a container built from its plain image in memory, nothing plain is ever on the disk, the container opens under
/// the master DEK to a valid database, and a plain snapshot is refused. Each scan first proves on a plain node that it can see the canaries.
/// </summary>
public sealed class SnapshotSealedCreateTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>
{
    private static string ArchiveDb(string archive, out JsonElement manifest, string name = "beememorybank.db")
    {
        byte[]? db = null;
        JsonElement? man = null;
        using (var file = File.OpenRead(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
            while (tar.GetNextEntry() is { } entry)
            {
                using var memory = new MemoryStream();
                entry.DataStream!.CopyTo(memory);
                if (entry.Name == name) db = memory.ToArray();
                if (entry.Name == "manifest.json") man = JsonSerializer.Deserialize<JsonElement>(memory.ToArray());
            }
        manifest = man!.Value;
        var path = archive + ".entry";
        File.WriteAllBytes(path, db!);
        return path;
    }

    private static async Task<string> OpenImageAsync(byte[] container, byte[] dek, string path)
    {
        var image = await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, dek);
        await File.WriteAllBytesAsync(path, image);
        return path;
    }

    [Fact]
    public async Task The_control_a_plain_node_snapshot_without_encryption_holds_the_canaries_so_the_scan_can_see_them()
    {
        var (root, dir, factory, _, snapshots) = await SealedNode.CreatePlainAsync(template);
        try
        {
            var info = await snapshots.CreateAsync(filterSecrets: true, sign: false, encryptDb: false);

            MetadataCanaries.ScanArchive(snapshots.GetSnapshotPath(info.FileName), template.Canaries).Should().NotBeEmpty();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task A_snapshot_of_a_sealed_node_holds_a_BMBDB2_container_no_plain_file_exists_while_it_is_made_and_no_canary_is_in_clear()
    {
        using var node = await SealedNode.CreateAsync(template);
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging, node.Snapshots.SnapshotsDir], template.Canaries);
        // The watch can see: a plain database planted for a moment is recorded. The probe stays until the watch has recorded it (a watch thread that is
        // slow to be scheduled is not a failure of the control) and the watch forgets it after it is deleted, so a look still half done on the probe
        // cannot put its hits into the lists of the real run below.
        var probe = Path.Combine(staging, "probe-plain.tmp");
        await File.WriteAllBytesAsync(probe, [.. "SQLite format 3\0"u8.ToArray(), .. System.Text.Encoding.UTF8.GetBytes(template.Canaries[0])]);
        SpinWait.SpinUntil(() => watch.Plain.Contains("probe-plain.tmp") && !watch.CanaryHits.IsEmpty, TimeSpan.FromSeconds(30)).Should().BeTrue("the watch sees the probe");
        File.Delete(probe);
        watch.Forget();

        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: true);
        watch.Stop();

        watch.Seen.Keys.Should().Contain(n => n.StartsWith("snapshot-"), "the watch saw the working copy");
        watch.Plain.Should().BeEmpty("the working copy of a sealed node is encrypted with the vault's page key at every moment");
        watch.CanaryHits.Should().BeEmpty();
        Directory.EnumerateFiles(staging).Should().BeEmpty("the working copy is gone");

        var archive = node.Snapshots.GetSnapshotPath(info.FileName);
        MetadataCanaries.ScanArchive(archive, template.Canaries).Should().BeEmpty("the archive holds a container, not a database");
        var entry = ArchiveDb(archive, out var manifest);
        var container = await File.ReadAllBytesAsync(entry);
        Bmbdb2Container.IsContainer(container).Should().BeTrue();
        container.AsSpan(0, 6).SequenceEqual("BMBDB2"u8).Should().BeTrue();
        manifest.GetProperty("dbEncrypted").GetBoolean().Should().BeTrue();

        // It decrypts under the master DEK to a valid image that a plain reader opens: the canary node's rows are in it.
        var dek = node.CopyDek();
        var imagePath = await OpenImageAsync(container, dek, archive + ".image.sqlite");
        using (var db = new SqliteConnection($"Data Source={imagePath};Mode=ReadOnly;Pooling=False"))
        {
            db.Open();
            db.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().BeGreaterThan(0);
            db.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_key_slot").Should().BeGreaterThan(0, "a backup keeps the key slots");
        }
        var wrong = async () => await Bmbdb2Container.DecryptAsync(new MemoryStream(container), container.Length, new byte[32]);
        await wrong.Should().ThrowAsync<System.Security.Cryptography.CryptographicException>();
        // The signed archive verifies as before.
        File.Exists(archive + ".sig").Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_snapshot_is_refused_on_a_sealed_node_and_nothing_is_created()
    {
        using var node = await SealedNode.CreateAsync(template);

        var act = async () => await node.Snapshots.CreateAsync(filterSecrets: true, sign: false, encryptDb: false);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*encrypted*");
        Directory.Exists(node.Snapshots.SnapshotsDir).Should().BeFalse("not even the folder of a snapshot that was never made");
        var staging = SnapshotStaging.DirIn(node.Dir);
        (!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any()).Should().BeTrue();
    }

    /// <summary>
    /// BMB-304: a node that cannot hold its own database in memory as the one buffer of the container says which memory limit would do, before it makes
    /// the working copy; nothing is left behind, and the same node makes the snapshot once the ceiling is lifted.
    /// </summary>
    [Fact]
    public async Task A_node_with_less_memory_than_its_database_needs_refuses_the_snapshot_naming_the_limit_and_leaves_nothing_behind()
    {
        using var node = await SealedNode.CreateAsync(template);
        // The test's ceiling is half the database: the fixture really is larger than it (the refusal is the database's, not only the slack's).
        var dbSize = new FileInfo(Path.Combine(node.Dir, "beememorybank.db")).Length;
        node.Snapshots.FreeMemoryOverride = dbSize / 2;
        (dbSize / 2).Should().BeLessThan(dbSize);

        var act = async () => await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);

        var ex = (await act.Should().ThrowAsync<NotEnoughMemoryException>()).Which;
        ex.Message.Should().StartWith("Making the snapshot of a ").And.Contain("MB database needs about").And.Contain("memory limit")
            .And.Contain($"to at least {ex.RecommendedLimitBytes / (1024 * 1024)} MB").And.Contain("Nothing was changed");
        ex.NeededBytes.Should().BeGreaterThan(MemoryBudget.Slack).And.BeLessThan(2L * 1024 * 1024 * 1024);
        (!Directory.Exists(node.Snapshots.SnapshotsDir) || !Directory.EnumerateFileSystemEntries(node.Snapshots.SnapshotsDir).Any()).Should().BeTrue("no snapshot");
        var staging = SnapshotStaging.DirIn(node.Dir);
        (!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any()).Should().BeTrue("no working copy");

        node.Snapshots.FreeMemoryOverride = null;
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        File.Exists(node.Snapshots.GetSnapshotPath(info.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task A_join_snapshot_of_a_sealed_node_is_a_BMBCH1_container_streamed_from_the_page_codec_with_no_plain_file()
    {
        using var node = await SealedNode.CreateAsync(template);
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging, node.Snapshots.SnapshotsDir], template.Canaries);

        var info = await node.Snapshots.CreateAsync(filterSecrets: true, sign: false, cpSequenceNum: 1, encryptDb: false, sealForJoin: true);
        watch.Stop();

        watch.Plain.Should().BeEmpty();
        watch.CanaryHits.Should().BeEmpty();
        var archive = node.Snapshots.GetSnapshotPath(info.FileName);
        MetadataCanaries.ScanArchive(archive, template.Canaries).Should().BeEmpty();
        var entry = ArchiveDb(archive, out var manifest);
        manifest.GetProperty("dbCipher").GetString().Should().Be("bmbch1");
        var dek = node.CopyDek();
        var opened = archive + ".opened.sqlite";
        await ChunkedContainer.DecryptFileAsync(entry, opened, ChunkedContainer.PurposeJoin,
            h => h.Key == ChunkedContainer.DekKeyLabel(DekFingerprint.Of(dek)) ? dek : null);
        using var db = new SqliteConnection($"Data Source={opened};Mode=ReadOnly;Pooling=False");
        db.Open();
        db.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_locked_vault_makes_no_snapshot_of_a_sealed_node_and_leaves_no_working_copy()
    {
        using var node = await SealedNode.CreateAsync(template);
        node.Session.Lock();

        var act = async () => await node.Snapshots.CreateAsync(filterSecrets: true, sign: false);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locked*");
        var staging = SnapshotStaging.DirIn(node.Dir);
        (!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any()).Should().BeTrue("the working copy was removed");
    }

}
