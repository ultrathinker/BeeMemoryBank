using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb snapshot create</c> on a sealed vault (docs/design/step-b-spec.md 8.3, red test 15.1.7, CLI part): the database entry is a BMBDB2
/// container built from the page codec under the master DEK; no plain SQLite file exists at any moment (the staging folder is watched while
/// the snapshot is made), and the archive holds no canary. The restore verb of the CLI refuses such a snapshot instead of copying the
/// container over a database.
/// </summary>
public sealed class SnapshotCommandSealedTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bmb_clisnap_" + Guid.NewGuid().ToString("N"));
    private readonly string _canary = MetadataCanaries.Ascii("title");
    private readonly InMemoryVaultKeySource _keys = new();
    private readonly byte[] _masterDek = RandomNumberGenerator.GetBytes(32);
    private readonly DbConnectionFactory _vault;

    public SnapshotCommandSealedTests()
    {
        Directory.CreateDirectory(_data);
        _vault = new DbConnectionFactory(_data, _keys);
        using var c = (SqliteConnection)_vault.CreateConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT); CREATE VIRTUAL TABLE fts_article USING fts5(title);";
        cmd.ExecuteNonQuery();
        for (var i = 0; i < 60; i++)
        {
            cmd.CommandText = "INSERT INTO tbl_article (title) VALUES ($t); INSERT INTO fts_article (rowid, title) VALUES (last_insert_rowid(), $t);";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$t", i % 10 == 0 ? _canary + " " + i : "note " + i);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _vault.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_data, true); } catch { }
    }

    [Fact]
    public async Task The_snapshot_of_a_sealed_vault_carries_a_BMBDB2_container_and_no_plain_file_exists_while_it_is_made()
    {
        var tmp = Path.Combine(_data, "tmp");
        var observed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var canaryHits = new System.Collections.Concurrent.ConcurrentBag<string>();
        var stop = false;
        using var started = new ManualResetEventSlim();
        var poller = new Thread(() =>
        {
            started.Set();
            while (!Volatile.Read(ref stop))
            {
                if (Directory.Exists(tmp))
                    foreach (var file in Directory.GetFiles(tmp))
                    {
                        observed.TryAdd(Path.GetFileName(file), 0);
                        try
                        {
                            foreach (var hit in MetadataCanaries.Scan(File.ReadAllBytes(file), [_canary], file)) canaryHits.Add(hit.Encoding);
                        }
                        catch (IOException) { }
                    }
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        poller.Start();
        started.Wait();

        var result = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node");
        Thread.Sleep(50);
        Volatile.Write(ref stop, true);
        poller.Join();

        observed.Keys.Should().OnlyContain(n => n.StartsWith("snapshot-") && (n.EndsWith(".tmp") || n.EndsWith(".tmp-journal")),
            "the only files that appear are the encrypted staging copy and the rollback journal VACUUM INTO keeps beside it (encrypted pages)");
        canaryHits.Should().BeEmpty("the staging copy is encrypted at every moment");
        Directory.GetFiles(tmp).Should().BeEmpty("the staging copy is gone");

        File.Exists(result.Path).Should().BeTrue();
        Path.GetDirectoryName(result.Path).Should().Be(Path.Combine(_data, "snapshots"));
        MetadataCanaries.ScanArchive(result.Path, [_canary]).Should().BeEmpty("the archive holds a container, not a database");

        var (dbEntry, manifest) = ReadArchive(result.Path);
        dbEntry.AsSpan(0, 6).SequenceEqual("BMBDB2"u8).Should().BeTrue();
        manifest.GetProperty("dbEncrypted").GetBoolean().Should().BeTrue();
        manifest.GetProperty("snapshotFormatVersion").GetInt32().Should().Be(4);
        manifest.GetProperty("nodeId").GetString().Should().Be("node-1");
        manifest.GetProperty("files").GetProperty("beememorybank.db").GetString().Should().Be(result.DbHash);
        Convert.ToHexStringLower(SHA256.HashData(dbEntry)).Should().Be(result.DbHash);
    }

    [Fact]
    public async Task The_container_decrypts_under_the_master_DEK_to_a_valid_image_with_the_vaults_rows_and_not_under_another_key()
    {
        var result = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node");
        var (dbEntry, _) = ReadArchive(result.Path);

        var image = await Bmbdb2Container.DecryptAsync(new MemoryStream(dbEntry), dbEntry.Length, _masterDek);
        var imagePath = Path.Combine(_data, "decrypted-image.sqlite");
        await File.WriteAllBytesAsync(imagePath, image);
        using (var c = new SqliteConnection($"Data Source={imagePath};Pooling=False;Mode=ReadOnly"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            cmd.ExecuteScalar().Should().Be("ok");
            cmd.CommandText = "SELECT count(*) FROM tbl_article WHERE title LIKE $c";
            cmd.Parameters.AddWithValue("$c", _canary + "%");
            Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(6, "the rows are in the snapshot");
            cmd.CommandText = "SELECT count(*) FROM fts_article WHERE fts_article MATCH 'note'";
            Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(54);
        }

        var wrong = async () => await Bmbdb2Container.DecryptAsync(new MemoryStream(dbEntry), dbEntry.Length, RandomNumberGenerator.GetBytes(32));
        await wrong.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task The_snapshot_takes_a_name_free_in_both_snapshot_folders_and_overwrites_nothing()
    {
        // The way-back snapshot of an encrypted node lives in pre-encryption/ and a restore by name looks in snapshots/ first: the same name in both
        // would open the wrong archive.
        var second = new DateTime(2026, 10, 13, 9, 30, 15, DateTimeKind.Utc);
        var wayBack = Path.Combine(_data, "pre-encryption", "bmb-snapshot-20261013-093015.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(wayBack)!);
        File.WriteAllText(wayBack, "the way-back");

        var first = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node", nameClock: () => second);
        var again = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node", nameClock: () => second);

        Path.GetFileName(first.Path).Should().Be("bmb-snapshot-20261013-093015-2.tar.gz");
        Path.GetFileName(again.Path).Should().Be("bmb-snapshot-20261013-093015-3.tar.gz");
        new FileInfo(first.Path).Length.Should().BeGreaterThan(100, "the first archive is not truncated by the second");
        File.ReadAllText(wayBack).Should().Be("the way-back", "a file of that name in the other folder is left as it is");
    }

    [Fact]
    public async Task A_vault_that_is_not_open_or_a_session_without_the_master_dek_answers_the_password_sentence_and_writes_nothing()
    {
        var output = new StringWriter();
        var closedKeys = new InMemoryVaultKeySource();
        closedKeys.Close();

        var notUsable = await SnapshotCommand.HandleSealedCreateAsync(_data, new TestGate(usable: false), _keys, _vault, _masterDek, null, output);
        var keysClosed = await SnapshotCommand.HandleSealedCreateAsync(_data, new TestGate(usable: true), closedKeys, _vault, _masterDek, null, output);
        var noDek = await SnapshotCommand.HandleSealedCreateAsync(_data, new TestGate(usable: true), _keys, _vault, null, null, output);
        var noKeys = await SnapshotCommand.HandleSealedCreateAsync(_data, new TestGate(usable: true), null, _vault, _masterDek, null, output);

        new[] { notUsable, keysClosed, noDek, noKeys }.Should().OnlyContain(rc => rc == 1);
        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Should()
            .OnlyContain(l => l == SnapshotCommand.SealedNeedsPassword).And.HaveCount(4);
        Directory.Exists(Path.Combine(_data, "snapshots")).Should().BeFalse();
    }

    [Fact]
    public async Task HandleSealedCreateAsync_reports_the_node_and_the_checksum_on_success()
    {
        var output = new StringWriter();
        var identity = new NodeIdentity { NodeId = Guid.NewGuid(), DisplayName = "Kitchen server" };

        var rc = await SnapshotCommand.HandleSealedCreateAsync(_data, new TestGate(usable: true), _keys, _vault, _masterDek, identity, output);

        rc.Should().Be(0);
        var text = output.ToString();
        text.Should().Contain("Snapshot created:").And.Contain("Kitchen server").And.Contain(identity.NodeId.ToString()).And.Contain("SHA256(db):");
        text.Should().NotContain(Convert.ToHexString(_keys.GetPageKey(DbPurposes.Vault)), "no key is ever printed");
    }

    [Fact]
    public async Task The_restore_verb_of_the_cli_refuses_a_snapshot_whose_database_is_encrypted_and_leaves_the_target_alone()
    {
        var result = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node");
        var target = Path.Combine(Path.GetTempPath(), "bmb_clisnap_target_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "beememorybank.db");
        await File.WriteAllBytesAsync(existing, [1, 2, 3, 4]);
        try
        {
            var output = new StringWriter();

            var rc = await SnapshotCommand.HandleRestoreAsync(target, result.Path, output);

            rc.Should().Be(1);
            output.ToString().Should().Contain("encrypted").And.Contain("restore-standalone");
            (await File.ReadAllBytesAsync(existing)).Should().Equal(1, 2, 3, 4);
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(target, true); } catch { }
        }
    }

    private static (byte[] Db, JsonElement Manifest) ReadArchive(string path)
    {
        byte[]? db = null;
        JsonElement? manifest = null;
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            using var memory = new MemoryStream();
            entry.DataStream!.CopyTo(memory);
            if (entry.Name == "beememorybank.db") db = memory.ToArray();
            if (entry.Name == "manifest.json") manifest = JsonSerializer.Deserialize<JsonElement>(memory.ToArray());
        }
        return (db!, manifest!.Value);
    }

    private sealed class TestGate(bool usable) : IVaultGate
    {
        public VaultState State => usable ? VaultState.Open : VaultState.Closed;
        public bool IsSealed => true;
        public bool IsUsable => usable;
        public bool IsHeld => false;
        public Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteOpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task WaitUsableAsync(CancellationToken ct) => Task.CompletedTask;
        public event Action<VaultState>? StateChanged { add { } remove { } }
    }
}
