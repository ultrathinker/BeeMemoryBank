using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>What a test does to the stream a file or an archive is written through.</summary>
internal sealed class FaultyStream(Stream inner, long failAfterBytes, Action onFault) : Stream
{
    private long _written;
    private bool _faulted;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Account(int count)
    {
        _written += count;
        if (_written <= failAfterBytes || _faulted) return;
        _faulted = true;
        onFault();   // throws the failure, or cancels the token and lets the write go on
    }

    public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Account(count); }
    public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); Account(buffer.Length); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        Account(buffer.Length);
    }

    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
}

/// <summary>
/// BMB-304 fix round, findings 2, 3, 5 and 7 at the level of the service: a legacy BMBDB1 snapshot (no page reserve) is restored from the one buffer it was
/// opened in; a container that does not authenticate leaves no plain bytes anywhere; the container is rewritten through a temp file that is renamed only when
/// whole, so a full volume or a cancellation leaves the file as it was; an archive that fails or is cancelled while it is written leaves nothing.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class SnapshotMemoryFixRoundTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>, IDisposable
{
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* the test's own temporary folder */ }
    }

    private const string Changed = "Changed after the snapshot";

    private sealed class NoHooks : ISealedRestoreHooks
    {
        public Task BeforeSwapAsync(SqliteConnection staged, byte[] stagedMasterDek, CancellationToken ct) => Task.CompletedTask;
        public Task AfterSwapAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static string DisplayName(DbConnectionFactory factory)
    {
        using var c = factory.CreateConnection();
        return c.ExecuteScalar<string>("SELECT display_name FROM tbl_node_identity")!;
    }

    private static void SetDisplayName(DbConnectionFactory factory, string name)
    {
        using var c = factory.CreateConnection();
        c.Execute("UPDATE tbl_node_identity SET display_name = @name", new { name });
    }

    private static bool StagingIsEmpty(string dir)
    {
        var staging = SnapshotStaging.DirIn(dir);
        return !Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any();
    }

    /// <summary>BMBDB1, the container a 2.5 node writes: <c>"BMBDB1" || iv(12) || tag(16) || ciphertext</c> under the master DEK itself, AAD "bmb-snap-db-v1".</summary>
    internal static byte[] LegacyContainer(byte[] masterDek, byte[] image)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var body = (byte[])image.Clone();
        using (var gcm = new AesGcm(masterDek, 16)) gcm.Encrypt(iv, body, body, tag, "bmb-snap-db-v1"u8);
        var container = new byte[6 + 12 + 16 + body.Length];
        "BMBDB1"u8.CopyTo(container);
        iv.CopyTo(container, 6);
        tag.CopyTo(container, 18);
        body.CopyTo(container, 34);
        return container;
    }

    /// <summary>The same archive with the database entry changed and the manifest told its new hash (the checks of a restore that come before the key).</summary>
    internal static void RewriteDatabaseEntry(string source, string target, Func<byte[], byte[]> change)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        using (var input = File.OpenRead(source))
        using (var gzipIn = new GZipStream(input, CompressionMode.Decompress))
        using (var reader = new TarReader(gzipIn))
            while (reader.GetNextEntry() is { } entry)
            {
                using var memory = new MemoryStream();
                entry.DataStream!.CopyTo(memory);
                entries.Add((entry.Name, memory.ToArray()));
            }

        var db = entries.FindIndex(e => e.Name == "beememorybank.db");
        var changed = change(entries[db].Bytes);
        entries[db] = (entries[db].Name, changed);
        var manifest = entries.FindIndex(e => e.Name == "manifest.json");
        var json = JsonNode.Parse(entries[manifest].Bytes)!.AsObject();
        json["files"]!.AsObject()["beememorybank.db"] = Convert.ToHexStringLower(SHA256.HashData(changed));
        entries[manifest] = (entries[manifest].Name, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));

        using var output = File.Create(target);
        using var gzipOut = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzipOut, TarEntryFormat.Pax);
        foreach (var (name, bytes) in entries)
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });
    }

    // ---------------------------------------------------------------- finding 2: a legacy BMBDB1 snapshot, restored for real

    [Fact]
    public async Task A_legacy_BMBDB1_snapshot_with_no_page_reserve_is_restored_into_the_sealed_node_holding_one_whole_image()
    {
        using var node = await SealedNode.CreateAsync(template);
        var plainImage = await File.ReadAllBytesAsync(Path.Combine(template.PlainDir, "beememorybank.db"));
        plainImage[20].Should().Be(0, "the control: a 2.5 database has no page reserve, so a restore takes the deserialized path");
        var container = LegacyContainer(node.CopyDek(), plainImage);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var original = DisplayName(node.Factory);
        const string legacy = "legacy-bmbdb1.tar.gz";
        RewriteDatabaseEntry(node.Snapshots.GetSnapshotPath(info.FileName), Path.Combine(node.Snapshots.SnapshotsDir, legacy), _ => container);
        node.Snapshots.SealedHooks = new NoHooks();
        SetDisplayName(node.Factory, Changed);
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging], template.Canaries);
        var log = new List<(string What, long Bytes, bool Native)>();
        MemoryBudget.Allocations.Value = log;

        var outcome = await node.Snapshots.RestoreAsync(legacy, masterPassword: SealedNodeTemplate.Password);
        watch.Stop();

        outcome.Standalone.Should().BeFalse();
        DisplayName(node.Factory).Should().Be(original, "the legacy snapshot's database is the node's now");
        log.Where(a => a.Native).Should().BeEmpty("no native copy of the image: it was imported where the container was opened");
        log.Where(a => a.Bytes >= container.Length).Should().ContainSingle("exactly one whole-image allocation, the buffer the container was opened in")
            .Which.What.Should().Be("Opening the database of a snapshot");
        watch.Plain.Should().BeEmpty("the legacy image is never a plain file on the disk");
        watch.CanaryHits.Should().BeEmpty();
        StagingIsEmpty(node.Dir).Should().BeTrue();
        VaultStoreFiles.HasSqliteMagic(Path.Combine(node.Dir, "beememorybank.db")).Should().BeFalse("the live file is still sealed");
        MetadataCanaries.ScanDirectory(node.Dir, template.Canaries).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- finding 7: a container that does not authenticate

    [Theory]
    [InlineData(40)]    // inside the tag
    [InlineData(600)]   // inside the ciphertext
    public async Task A_container_with_a_changed_byte_is_refused_and_no_plain_byte_is_left_anywhere(int offset)
    {
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        SetDisplayName(node.Factory, Changed);
        const string tampered = "tampered-container.tar.gz";
        RewriteDatabaseEntry(node.Snapshots.GetSnapshotPath(info.FileName), Path.Combine(node.Snapshots.SnapshotsDir, tampered), db =>
        {
            var copy = (byte[])db.Clone();
            copy[offset] ^= 0xFF;
            return copy;
        });
        var hooks = new NoHooks();
        node.Snapshots.SealedHooks = hooks;
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging], template.Canaries);

        var act = async () => await node.Snapshots.RestoreAsync(tampered, masterPassword: SealedNodeTemplate.Password);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("cannot be opened here").And.Contain("Nothing was changed");
        watch.Stop();
        watch.Plain.Should().BeEmpty("the buffer the container was opened in is cleared, and nothing plain was written");
        watch.CanaryHits.Should().BeEmpty();
        StagingIsEmpty(node.Dir).Should().BeTrue();
        DisplayName(node.Factory).Should().Be(Changed, "the live vault is untouched");
        MetadataCanaries.ScanDirectory(node.Dir, template.Canaries).Should().BeEmpty();
    }

    [Fact]
    public void A_snapshot_container_refusal_carries_a_fixed_sentence_and_the_status_of_a_bad_request()
    {
        var inner = new InvalidDataException(@"cannot read C:\private\layout\beememorybank.db");

        var refusal = new SnapshotContainerException(inner);

        refusal.Message.Should().NotContain("private").And.Contain("damaged, cut short, or was made by another vault").And.Contain("Nothing was changed");
        refusal.InnerException.Should().BeSameAs(inner);
        ExceptionStatusMap.Map(refusal).StatusCode.Should().Be(400, "the request is what is wrong, not the node");
        ExceptionStatusMap.Details(refusal).Code.Should().Be("bad_snapshot");
    }

    // ---------------------------------------------------------------- finding 3: the container is rewritten through a temp file

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_rewrite_" + Guid.NewGuid().ToString("N"));

    private (string Path, byte[] Dek, byte[] Image) NewImageFile(string name)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        var image = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);   // several blocks: a write can fail after the first
        File.WriteAllBytes(path, image);
        return (path, RandomNumberGenerator.GetBytes(32), image);
    }

    private void NothingElseIsLeftIn(string expectedFile) =>
        Directory.EnumerateFileSystemEntries(_dir).Should().BeEquivalentTo([expectedFile], "no temp file, no half-written sibling");

    /// <summary>A sealed staging copy of the fixture database under a throw-away page key, the way the snapshot flows hold it before they build a container from it.</summary>
    private async Task<(string Path, byte[] PageKey)> SealedStagingAsync(string name)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        var pageKey = RandomNumberGenerator.GetBytes(32);
        await using var plain = File.OpenRead(Path.Combine(template.PlainDir, "beememorybank.db"));
        await PlainImageImporter.ToSealedFileAsync(plain, plain.Length, path, pageKey, PlainImageImporter.DefaultMemoryCap());
        return (path, pageKey);
    }

    /// <summary>BMB-304 fix2 (b): the sealed staging containers (the BMBDB2 of a snapshot, the BMBCH1 of a join) are written through a rename of a finished file and take the cancellation: a token that is cancelled leaves the staging copy as it was.</summary>
    [Fact]
    public async Task A_sealed_staging_container_made_under_a_cancelled_token_leaves_the_staging_copy_as_it_was_and_no_temp_file()
    {
        using var node = await SealedNode.CreateAsync(template);
        var keys = new SessionSnapshotKeyOperations(node.Session);
        var (path, pageKey) = await SealedStagingAsync("staging.db");
        var before = await File.ReadAllBytesAsync(path);
        var cancelled = new CancellationToken(canceled: true);

        await ((Func<Task>)(() => keys.EncryptSealedDatabaseAsync(path, pageKey, cancelled))).Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(before, "the snapshot's container was not written over the staging copy");
        NothingElseIsLeftIn(path);

        await ((Func<Task>)(() => keys.SealJoinSealedDatabaseAsync(path, pageKey, cancelled))).Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(before, "nor was the join's");
        NothingElseIsLeftIn(path);
    }

    [Fact]
    public async Task A_sealed_staging_container_is_written_whole_under_the_staging_name_and_nothing_else_is_left()
    {
        using var node = await SealedNode.CreateAsync(template);
        var keys = new SessionSnapshotKeyOperations(node.Session);
        var (snapshot, snapshotKey) = await SealedStagingAsync("snapshot.db");
        var (join, joinKey) = await SealedStagingAsync("join.db");

        (await keys.EncryptSealedDatabaseAsync(snapshot, snapshotKey)).Should().BeTrue();
        await keys.SealJoinSealedDatabaseAsync(join, joinKey);

        System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(snapshot))[..6]).Should().Be("BMBDB2");
        System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(join))[..6]).Should().Be("BMBCH1");
        Directory.EnumerateFileSystemEntries(_dir).Should().BeEquivalentTo([snapshot, join], "no temp file, no `.sealed` leftover");
    }

    [Fact]
    public async Task A_decrypt_whose_write_hits_a_full_volume_leaves_the_container_as_it_was_and_no_temp_file()
    {
        var (path, dek, image) = NewImageFile("vault.db");
        await SnapshotService.EncryptDbFileAsync(path, dek);
        var container = await File.ReadAllBytesAsync(path);

        var act = async () => await SnapshotService.DecryptDbFileAsync(path, dek,
            wrapWrite: s => new FaultyStream(s, failAfterBytes: 1024 * 1024, () => throw new IOException("There is not enough space on the disk.")));

        await act.Should().ThrowAsync<IOException>().WithMessage("*space*");
        (await File.ReadAllBytesAsync(path)).Should().Equal(container, "the authenticated container is still whole where it was");
        NothingElseIsLeftIn(path);
        // The same call once the volume has room.
        await SnapshotService.DecryptDbFileAsync(path, dek);
        (await File.ReadAllBytesAsync(path)).Should().Equal(image);
        NothingElseIsLeftIn(path);
    }

    [Fact]
    public async Task A_decrypt_that_is_cancelled_while_it_writes_leaves_the_container_as_it_was_and_no_temp_file()
    {
        var (path, dek, _) = NewImageFile("vault.db");
        await SnapshotService.EncryptDbFileAsync(path, dek);
        var container = await File.ReadAllBytesAsync(path);
        using var cts = new CancellationTokenSource();

        var act = async () => await SnapshotService.DecryptDbFileAsync(path, dek, ct: cts.Token,
            wrapWrite: s => new FaultyStream(s, failAfterBytes: 0, cts.Cancel));

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(container);
        NothingElseIsLeftIn(path);
    }

    [Fact]
    public async Task An_encrypt_whose_write_hits_a_full_volume_or_is_cancelled_leaves_the_file_as_it_was_and_no_temp_file()
    {
        var (path, dek, image) = NewImageFile("working.db");

        var full = async () => await SnapshotService.EncryptDbFileAsync(path, dek,
            wrapWrite: s => new FaultyStream(s, failAfterBytes: 1024 * 1024, () => throw new IOException("There is not enough space on the disk.")));
        await full.Should().ThrowAsync<IOException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(image, "the working copy is not half a container");
        NothingElseIsLeftIn(path);

        using var cts = new CancellationTokenSource();
        var cancelled = async () => await SnapshotService.EncryptDbFileAsync(path, dek, ct: cts.Token, wrapWrite: s => new FaultyStream(s, 0, cts.Cancel));
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(image);
        NothingElseIsLeftIn(path);

        var already = new CancellationToken(canceled: true);
        await ((Func<Task>)(() => SnapshotService.DecryptDbFileAsync(path, dek, ct: already))).Should().NotThrowAsync("a plain file passes through untouched");
        await ((Func<Task>)(() => SnapshotService.EncryptDbFileAsync(path, dek, ct: already))).Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(image);
        NothingElseIsLeftIn(path);
    }

    // ---------------------------------------------------------------- finding 5: an archive that fails or is cancelled while it is written

    private static List<string> Entries(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToList()! : [];

    [Fact]
    public async Task A_full_volume_or_a_cancellation_while_the_archive_is_written_leaves_no_archive_and_no_temp_file()
    {
        using var node = await SealedNode.CreateAsync(template);
        node.Snapshots.WrapArchiveStream = s => new FaultyStream(s, failAfterBytes: 0, () => throw new IOException("There is not enough space on the disk."));

        var full = async () => await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        await full.Should().ThrowAsync<IOException>().WithMessage("*space*");
        Entries(node.Snapshots.SnapshotsDir).Should().BeEmpty("nothing that is, or holds half of, a snapshot");
        StagingIsEmpty(node.Dir).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        node.Snapshots.WrapArchiveStream = s => new FaultyStream(s, failAfterBytes: 0, cts.Cancel);
        var cancelled = async () => await node.Snapshots.CreateAsync(filterSecrets: false, sign: false, ct: cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        Entries(node.Snapshots.SnapshotsDir).Should().BeEmpty();
        StagingIsEmpty(node.Dir).Should().BeTrue();

        // And the node makes the next one: one complete archive, owner-only, with its sidecar signature when it is signed.
        node.Snapshots.WrapArchiveStream = null;
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: true);
        Entries(node.Snapshots.SnapshotsDir).Should().BeEquivalentTo([info.FileName, info.FileName + ".sig"]);
        OwnerOnlyFile.IsOwnerOnly(node.Snapshots.GetSnapshotPath(info.FileName)).Should().BeTrue("a snapshot is a copy of the vault: owner-only from its first byte");
    }

    // ---------------------------------------------------------------- finding 7: the fixture really exceeds the ceiling

    [Fact]
    public async Task A_snapshot_of_a_database_larger_than_the_ceiling_is_refused_because_of_the_database_not_the_slack()
    {
        using var node = await SealedNode.CreateAsync(template);
        var dbSize = new FileInfo(Path.Combine(node.Dir, "beememorybank.db")).Length;
        var ceiling = dbSize / 2;
        ceiling.Should().BeLessThan(dbSize, "the control: the fixture database really is larger than the ceiling it is refused against");
        node.Snapshots.FreeMemoryOverride = ceiling;

        var act = async () => await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);

        var ex = (await act.Should().ThrowAsync<NotEnoughMemoryException>()).Which;
        ex.FreeBytes.Should().Be(ceiling);
        ex.NeededBytes.Should().BeGreaterThan(ceiling + MemoryBudget.Slack - 1, "image + container overhead + slack");
        Entries(node.Snapshots.SnapshotsDir).Should().BeEmpty();
    }

    [Fact]
    public async Task The_memory_check_of_a_snapshot_counts_the_live_pages_not_the_free_ones()
    {
        using var node = await SealedNode.CreateAsync(template);
        using (var c = node.Factory.CreateConnection())
        {
            c.Execute("CREATE TABLE bulk (id INTEGER PRIMARY KEY, filler BLOB)");
            for (var i = 0; i < 200; i++) c.Execute("INSERT INTO bulk (filler) VALUES (randomblob(8192))");
            c.Execute("DROP TABLE bulk");   // the pages stay in the file, on the free list
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        var fileSize = new FileInfo(Path.Combine(node.Dir, "beememorybank.db")).Length;
        long live;
        using (var c = node.Factory.CreateConnection()) live = Bmbdb2Container.LiveImageBytes(c, fileSize);

        live.Should().BeLessThan(fileSize - 1024 * 1024, "about 1.6 MB of free pages are in the file and not in the image VACUUM INTO will make");
        node.Snapshots.FreeMemoryOverride = live + Bmbdb2Container.OverheadV2 + MemoryBudget.Slack;   // enough for the live image, not for the file

        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);

        File.Exists(node.Snapshots.GetSnapshotPath(info.FileName)).Should().BeTrue("a snapshot that fits is not refused for pages it will not hold");
    }
}

/// <summary>
/// BMB-304 fix round, finding 4 and 7: the real <c>POST /api/snapshots/restore</c>. A node with too little memory answers 507 with the machine-readable code
/// (the route used to catch <c>InvalidOperationException</c> first and answer 400 without it), and a container that does not authenticate is a safe 400 that
/// changes nothing.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class SnapshotRestoreEndpointFailureTests : IAsyncLifetime
{
    private const string Password = "restoreFailurePw1";
    private readonly BmbWebApplicationFactory _node = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _node.CreateClient();
        await _node.InitializeEncryptedNodeAsync(displayName: "FailureNode", password: Password);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private SnapshotService Snapshots => _node.Services.GetRequiredService<SnapshotService>();

    private async Task<string> IdentityAsync()
    {
        var session = _node.Services.GetRequiredService<SessionService>();
        if (!session.IsUnlocked) (await session.UnlockAsync(Password)).Should().BeTrue();
        using var c = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return c.ExecuteScalar<string>("SELECT node_id || '|' || display_name FROM tbl_node_identity")!;
    }

    [Fact]
    public async Task A_restore_on_a_node_without_the_memory_for_it_answers_507_with_the_code_and_changes_nothing()
    {
        var info = await Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var before = await IdentityAsync();
        var dbSize = new FileInfo(Path.Combine(_node.DataPath, "beememorybank.db")).Length;
        Snapshots.FreeMemoryOverride = dbSize / 2;   // the fixture database is larger than this ceiling

        var response = await _client.PostAsJsonAsync("/api/snapshots/restore", new { fileName = info.FileName, masterPassword = Password, createBackupFirst = false, mode = "keep-identity" });

        response.StatusCode.Should().Be((HttpStatusCode)507, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("not_enough_memory", "a caller can act on the code without parsing the sentence");
        body.GetProperty("error").GetString().Should().Contain("Opening the database of a snapshot needs about").And.Contain("memory limit").And.Contain("Nothing was changed");
        (await IdentityAsync()).Should().Be(before, "the live vault is untouched");

        Snapshots.FreeMemoryOverride = null;
        var again = await _client.PostAsJsonAsync("/api/snapshots/restore", new { fileName = info.FileName, masterPassword = Password, createBackupFirst = false, mode = "keep-identity" });
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
    }

    /// <summary>A SQLite file header (100 bytes) followed by nothing: pages of zeros, as many as <paramref name="pages"/>. <paramref name="reserved"/> is byte 20 of the header.</summary>
    private static byte[] HeaderOnlyImage(int pages, byte reserved)
    {
        var image = new byte[pages * 4096];
        "SQLite format 3\0"u8.CopyTo(image);
        image[16] = 0x10; image[17] = 0x00;   // 4096-byte pages
        image[18] = 1; image[19] = 1;
        image[20] = reserved;
        image[21] = 64; image[22] = 32; image[23] = 32;
        return image;
    }

    /// <summary>BMB-304 fix2 (P2): a container that AUTHENTICATES but holds no readable database is the same safe 400 as any other damaged snapshot - never a 500, never the parser's text.</summary>
    [Theory]
    [InlineData("not a database at all", 0, 0)]
    [InlineData("a header and no pages (no reserve)", 1, 0)]
    [InlineData("a header and no pages (page reserve)", 1, 32)]
    public async Task A_container_that_authenticates_but_holds_no_readable_database_answers_a_safe_400_and_changes_nothing(string what, int pages, int reserved)
    {
        var info = await Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var before = await IdentityAsync();
        var image = pages == 0 ? RandomNumberGenerator.GetBytes(5000) : HeaderOnlyImage(pages * 8, (byte)reserved);
        var container = SnapshotMemoryFixRoundTests.LegacyContainer(_node.Services.GetRequiredService<SessionService>().GetMasterDek(), image);
        const string damaged = "authenticated-but-not-a-database.tar.gz";
        SnapshotMemoryFixRoundTests.RewriteDatabaseEntry(Snapshots.GetSnapshotPath(info.FileName), Path.Combine(Snapshots.SnapshotsDir, damaged), _ => container);

        var response = await _client.PostAsJsonAsync("/api/snapshots/restore", new { fileName = damaged, masterPassword = Password, createBackupFirst = false, mode = "keep-identity" });

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, what + ": " + text);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("code").GetString().Should().Be("bad_snapshot", what);
        body.GetProperty("error").GetString().Should().Contain("cannot be opened").And.Contain("Nothing was changed").And.NotContain("SQLite").And.NotContain(_node.DataPath);
        (await IdentityAsync()).Should().Be(before, "the live vault is untouched");
        var staging = SnapshotStaging.DirIn(_node.DataPath);
        (!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any()).Should().BeTrue("no staged or plain byte of the image is left");
    }

    [Fact]
    public async Task A_container_that_does_not_authenticate_answers_a_safe_400_and_changes_nothing()
    {
        var info = await Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var before = await IdentityAsync();
        const string tampered = "tampered-through-the-route.tar.gz";
        SnapshotMemoryFixRoundTests.RewriteDatabaseEntry(Snapshots.GetSnapshotPath(info.FileName), Path.Combine(Snapshots.SnapshotsDir, tampered), db =>
        {
            var copy = (byte[])db.Clone();
            copy[40] ^= 0xFF;   // inside the GCM tag
            return copy;
        });

        var response = await _client.PostAsJsonAsync("/api/snapshots/restore", new { fileName = tampered, masterPassword = Password, createBackupFirst = false, mode = "keep-identity" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        error.Should().Contain("cannot be opened here").And.Contain("Nothing was changed").And.NotContain(_node.DataPath);
        (await IdentityAsync()).Should().Be(before);
        var staging = SnapshotStaging.DirIn(_node.DataPath);
        (!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any()).Should().BeTrue("nothing of the container or its image is left");
    }
}
