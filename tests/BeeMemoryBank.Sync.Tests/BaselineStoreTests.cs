using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The blind baseline store (docs/design/step-a-spec.md 9, red tests 16.3 items 3-6, 10): adoption only forward and only
/// from an authorized superadmin producer with every blob present; the first adoption purges the legacy plaintext content
/// from the database file and its WAL; trimming bounded by the baseline once activated; blob clean-up by references.
/// </summary>
public sealed class BaselineStoreTests : IAsyncLifetime
{
    private Format2TestNode _node = null!;
    private Format2TestKit.Producer _producer = null!;
    private SyncProtocolPolicy _policy = null!;
    private BaselineStore _store = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb_bstore_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _blobs = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_work);
        _node = await Format2TestNode.CreateAsync(nodeId: BlindNodeId.NewId(), name: "blind");
        _producer = Format2TestKit.Producer.Create();
        await _node.TrustAsync(_producer.NodeId, _producer.PublicKey, superadmin: true, name: "producer");
        _policy = new SyncProtocolPolicy(_node.Factory);
        _store = new BaselineStore(_node.Factory, _node.DataPath, _policy);
        for (var i = 0; i < 2; i++) _blobs.Add(await Format2TestKit.StoreBlobAsync(_node.Factory));
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private BaselineFiles Baseline(long lamport = 100, int generation = 1, IEnumerable<string>? blobs = null,
        Format2TestKit.Producer? producer = null, Action<string>? tamper = null) =>
        Format2TestKit.WriteBaseline(Path.Combine(_work, Guid.NewGuid().ToString("N")), producer ?? _producer, blobs ?? _blobs,
            lamport, generation, tamper: tamper);

    [Fact]
    public async Task Adopt_WritesTheRowItsBlobListAndTheFiles()
    {
        var files = Baseline();
        var result = await _store.AdoptAsync(files, localCut: 4, producerAuthorized: true);

        result.Outcome.Should().Be(AdoptOutcome.Adopted, result.Reason);
        var current = await _store.GetCurrentAsync();
        current!.Files.Manifest.BaselineId.Should().Be(files.Manifest.BaselineId);
        current.LocalCut.Should().Be(4);
        current.Files.Directory.Should().Be(_store.CurrentDirectory);
        File.ReadAllBytes(Path.Combine(_store.CurrentDirectory, BaselineManifest.ContainerFileName))
            .Should().Equal(File.ReadAllBytes(Path.Combine(files.Directory, BaselineManifest.ContainerFileName)));
        File.ReadAllBytes(Path.Combine(_store.CurrentDirectory, BaselineManifest.FileName)).Should().Equal(files.ManifestBytes);
        (await _node.CountAsync("tbl_baseline_blob")).Should().Be(_blobs.Count);
        (await _store.AdoptAsync(files, 4, true)).Outcome.Should().Be(AdoptOutcome.AlreadyCurrent);
    }

    [Fact]
    public async Task ANewerBaseline_ReplacesTheCurrentOne_RowsAndFiles()
    {
        await _store.AdoptAsync(Baseline(lamport: 100), 4, true);
        var newer = Baseline(lamport: 200, generation: 2, blobs: _blobs.Take(1));
        (await _store.AdoptAsync(newer, 6, true)).Outcome.Should().Be(AdoptOutcome.Adopted);

        (await _node.CountAsync("tbl_baseline")).Should().Be(1, "the previous row is cleared");
        (await _node.CountAsync("tbl_baseline_blob")).Should().Be(1, "and its blob list with it");
        (await _store.GetCurrentAsync())!.Files.Manifest.BaselineId.Should().Be(newer.Manifest.BaselineId);
        Directory.GetDirectories(_store.BaselinesDirectory).Select(Path.GetFileName).Should().Equal(["current"],
            "the previous files are deleted and nothing is left staged");
    }

    public static IEnumerable<object[]> Refusals() =>
    [
        ["not authorized by the caller", "producer is not authorized"],
        ["producer not superadmin here", "not a superadmin here"],
        ["producer revoked here", "not an active node here"],
        ["bad signature", "signature does not verify"],
        ["container hash", "container does not match"],
        ["blob list hash", "blob list does not match"],
        ["older lamport_at_cut", "older than the current baseline"],
        ["older generation", "older key generation"],
        ["lower local cut", "covers less of this node's log"],
        ["missing blobs", "are not stored here"],
        ["a plaintext database", "plain SQLite database"],
    ];

    /// <summary>Spec 9.2 and 16.3 item 3: each refusal leaves the current baseline as it was.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Adopt_Refuses(string what, string reason)
    {
        var first = Baseline(lamport: 100, generation: 2);
        (await _store.AdoptAsync(first, 10, true)).Outcome.Should().Be(AdoptOutcome.Adopted);

        BaselineFiles candidate = Baseline(lamport: 150, generation: 2);
        long cut = 12;
        var authorized = true;
        switch (what)
        {
            case "not authorized by the caller": authorized = false; break;
            case "producer not superadmin here": await SetProducerAsync("is_superadmin = 0"); break;
            case "producer revoked here": await SetProducerAsync("status = 'R'"); break;
            case "bad signature": candidate = candidate with { Signature = Flip(candidate.Signature) }; break;
            case "container hash": candidate = Baseline(150, 2, tamper: d => FlipFile(Path.Combine(d, BaselineManifest.ContainerFileName))); break;
            case "blob list hash": candidate = Baseline(150, 2, tamper: d => File.WriteAllBytes(Path.Combine(d, BaselineManifest.BlobListFileName), Format2Package.FormatBlobList(_blobs.Take(1)))); break;
            case "older lamport_at_cut": candidate = Baseline(lamport: 99, generation: 2); break;
            case "older generation": candidate = Baseline(lamport: 150, generation: 1); break;
            case "lower local cut": cut = 9; break;
            case "missing blobs": candidate = Baseline(150, 2, blobs: [.. _blobs, new string('e', 64)]); break;
            case "a plaintext database":
                candidate = Format2TestKit.WriteBaseline(Path.Combine(_work, "plain"), _producer, _blobs, 150, 2,
                    container: [.. "SQLite format 3\0"u8.ToArray(), .. new byte[100]]);
                break;
        }

        var result = await _store.AdoptAsync(candidate, cut, authorized);
        result.Outcome.Should().Be(AdoptOutcome.Refused, what);
        result.Reason.Should().Contain(reason);
        var current = await _store.GetCurrentAsync();
        current!.Files.Manifest.BaselineId.Should().Be(first.Manifest.BaselineId, "a refusal changes nothing");
        current.LocalCut.Should().Be(10);
    }

    /// <summary>
    /// Spec 9.3 and 16.3 item 4: a 2.5 blind node materialised the vault's metadata. The first adoption deletes every row of
    /// every non-operational table and every protocol-3 content event with secure_delete, empties the full-text indexes,
    /// rebuilds the file and truncates the WAL: the canaries are in the file and WAL before (control) and nowhere after.
    /// </summary>
    [Fact]
    public async Task TheFirstAdoption_PurgesTheLegacyContent_FromTheFileAndItsWal()
    {
        var canaries = await SeedLegacyContentAsync();
        var sealedEvent = Format2TestKit.SealedEvent(_producer.NodeId, _producer.PrivateKey, 50);
        await _node.Events.AppendAsync(sealedEvent);

        var before = MetadataCanaries.ScanDirectory(_node.DataPath, canaries);
        before.Should().NotBeEmpty("the scanner sees the legacy content where it is");

        (await _store.AdoptAsync(Baseline(), 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);

        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(_node.DataPath, canaries).Should().BeEmpty(
            "no page of the legacy content survives in the database, its WAL or its journal");
        foreach (var table in await NonOperationalTablesWithRowsAsync())
            Assert.Fail($"{table} still holds rows after the purge");
        (await _node.Events.ExistsAsync(sealedEvent.EventId)).Should().BeTrue("sealed events are what a blind host keeps");
        (await _node.CountAsync("tbl_whitelist")).Should().BeGreaterThan(0, "the node keeps its peers");
        using var conn = _node.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<string>("SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'")).Should().Be("1");
    }

    [Fact]
    public async Task OnlyTheFirstAdoption_Purges()
    {
        (await _store.AdoptAsync(Baseline(lamport: 100), 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);
        // A clear event stored afterwards (it cannot reach an activated host; here it stands for any row the purge would delete).
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync(
                "INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES ('f', '/x', 'x', 'now', 'now')");
        (await _store.AdoptAsync(Baseline(lamport: 101), 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);
        (await _node.CountAsync("tbl_folder")).Should().Be(1, "the purge runs once per host; PurgeLegacyContentAsync stays callable");
        await _store.PurgeLegacyContentAsync();
        (await _node.CountAsync("tbl_folder")).Should().Be(0);
    }

    /// <summary>A purge that did not finish (the flag is not set) is retried by the next adoption, also of the baseline already current.</summary>
    [Fact]
    public async Task APurgeThatDidNotFinish_IsRetried_ByTheNextAdoption()
    {
        var files = Baseline(lamport: 100);
        (await _store.AdoptAsync(files, 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);
        // The legacy content is back and the flag is gone: what a failed VACUUM or a crash after the row switch leaves.
        using (var conn = _node.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES ('f', '/x', 'x', 'now', 'now')");
            await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = 'opaque_store'");
        }

        (await _store.AdoptAsync(files, 0, true)).Outcome.Should().Be(AdoptOutcome.AlreadyCurrent);
        (await _node.CountAsync("tbl_folder")).Should().Be(0, "the same baseline again finishes the purge");

        using (var conn = _node.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES ('g', '/y', 'y', 'now', 'now')");
            await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = 'opaque_store'");
        }
        (await _store.AdoptAsync(Baseline(lamport: 101), 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);
        (await _node.CountAsync("tbl_folder")).Should().Be(0, "and so does a newer baseline");
    }

    /// <summary>The operational list (spec 9.1) is pinned in docs/blind-node/OPERATIONAL-TABLES.golden.txt and names only real tables.</summary>
    [Fact]
    public async Task TheOperationalTables_AreTheGolden_AndExistInTheSchema()
    {
        var golden = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "blind-node", "OPERATIONAL-TABLES.golden.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        BaselineStore.OperationalTables.Should().Equal(golden, "a change to what a blind host keeps is a reviewed change");
        using var conn = _node.Factory.CreateConnection();
        var schema = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet();
        BaselineStore.OperationalTables.Should().OnlyContain(t => schema.Contains(t));
        foreach (var content in new[] { "tbl_folder", "tbl_article", "tbl_article_body", "tbl_article_version", "tbl_conflict_version",
                     "tbl_concept_tag", "tbl_article_concept_tag", "tbl_concept_tag_edge", "tbl_media", "tbl_tombstone",
                     "tbl_comment", "tbl_projection_matrix", "tbl_hard_delete_audit" })
            BaselineStore.OperationalTables.Should().NotContain(content);
    }

    /// <summary>Spec 9.5 and 16.3 item 5.</summary>
    [Fact]
    public async Task Trimming_IsBoundedByTheBaseline_AndNothingGoesWhileActivatedWithoutOne()
    {
        var reader = Guid.NewGuid();
        await _node.TrustAsync(reader, new byte[32], superadmin: false, name: "reader");
        for (var i = 1; i <= 6; i++)
            await _node.Events.AppendAsync(Format2TestKit.SealedEvent(_producer.NodeId, _producer.PrivateKey, i));
        var push = new SyncPushPositionRepository(_node.Factory);
        await push.RecordReportedPositionAsync(reader, 5);
        await push.UpdatePositionAsync(reader, 5);
        await push.RecordReportedPositionAsync(_producer.NodeId, 6);
        await push.UpdatePositionAsync(_producer.NodeId, 6);
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES ('a1', @author, 'fp', @vector, 'd', 'h', @at, 10)",
                new { author = _producer.NodeId.ToString(), vector = $"{{\"{_producer.NodeId}\": 6}}", at = DateTime.UtcNow.ToString("O") });

        // Activated, no baseline: the deltas are the only copy, nothing is trimmed.
        await Format2TestKit.ActivateAsync(_node.Factory, _producer.NodeId);
        _policy.MarkActivated();
        (await _store.TrimAsync()).Should().Be(0);
        (await _node.Events.GetMinSequenceAsync()).Should().Be(1);

        // With a baseline covering up to 3 of this log: read by all (5) and the anchor (6) would allow more.
        (await _store.AdoptAsync(Baseline(), localCut: 3, producerAuthorized: true)).TrimmedEvents.Should().Be(3);
        (await _node.Events.GetMinSequenceAsync()).Should().Be(4);
        (await new EventLogRepository(_node.Factory).GetLastCompactionCpAsync()).Should().Be(3, "a peer below the cut gets 410");

        // A newer baseline covering more: now read-by-all (5) is the bound.
        (await _store.AdoptAsync(Baseline(lamport: 300), localCut: 6, producerAuthorized: true)).TrimmedEvents.Should().Be(2);
        (await _node.Events.GetMinSequenceAsync()).Should().Be(6);
    }

    [Fact]
    public async Task BeforeActivation_TrimmingIsAsIn251()
    {
        var reader = Guid.NewGuid();
        await _node.TrustAsync(reader, new byte[32], superadmin: false, name: "reader");
        for (var i = 1; i <= 4; i++)
            await _node.Events.AppendAsync(Format2TestKit.SealedEvent(_producer.NodeId, _producer.PrivateKey, i));
        var push = new SyncPushPositionRepository(_node.Factory);
        await push.RecordReportedPositionAsync(reader, 3);
        await push.RecordReportedPositionAsync(_producer.NodeId, 4);
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES ('a1', @author, 'fp', @vector, 'd', 'h', @at, 10)",
                new { author = _producer.NodeId.ToString(), vector = $"{{\"{_producer.NodeId}\": 4}}", at = DateTime.UtcNow.ToString("O") });

        (await _store.TrimAsync()).Should().Be(3, "not activated: read by all (3) and covered by the anchor (4), no baseline needed");
    }

    /// <summary>Spec 9.6 and 16.3 item 6, run by every adoption.</summary>
    [Fact]
    public async Task BlobCleanUp_KeepsBaselineAndDeltaReferences_DropsTheRest()
    {
        var deltaBlob = await Format2TestKit.StoreBlobAsync(_node.Factory);
        var orphan = await Format2TestKit.StoreBlobAsync(_node.Factory);
        var young = await Format2TestKit.StoreBlobAsync(_node.Factory);
        await _node.Events.AppendAsync(Format2TestKit.SealedEvent(_producer.NodeId, _producer.PrivateKey, 7, [deltaBlob]));
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_blob SET created_at = @old WHERE hash <> @young",
                new { old = DateTime.UtcNow.AddDays(-2).ToString("o"), young });

        var result = await _store.AdoptAsync(Baseline(), 0, true);
        result.CollectedBlobs.Should().Be(1);
        var blobs = new BlobRepository(_node.Factory);
        foreach (var kept in _blobs.Append(deltaBlob).Append(young))
            (await blobs.GetAsync(kept)).Should().NotBeNull();
        (await blobs.GetAsync(orphan)).Should().BeNull();
    }

    [Fact]
    public async Task ASwitchCutShortByACrash_IsFinishedFromTheStagedFiles()
    {
        var files = Baseline();
        await _store.AdoptAsync(files, 0, true);
        // The crash: the rows switched, the directories did not.
        var staged = Path.Combine(_store.BaselinesDirectory, "incoming-" + files.Manifest.BaselineId.ToString("N"));
        Directory.Move(_store.CurrentDirectory, staged);

        var current = await _store.GetCurrentAsync();
        current.Should().NotBeNull();
        File.ReadAllBytes(Path.Combine(_store.CurrentDirectory, BaselineManifest.FileName)).Should().Equal(files.ManifestBytes);
        Directory.Exists(staged).Should().BeFalse();
    }

    public static IEnumerable<object[]> NotAContainer() => Format2TestKit.NotAContainerCases.Select(c => new object[] { c });

    /// <summary>Spec 7.1 (review round): the store takes only a BMBCH1-framed container, whatever else it is signed as.</summary>
    [Theory]
    [MemberData(nameof(NotAContainer))]
    public async Task Adopt_RefusesAContainerThatIsNotBmbch1(string what)
    {
        var first = Baseline(lamport: 100);
        (await _store.AdoptAsync(first, 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);

        var bad = Format2TestKit.WriteBaseline(Path.Combine(_work, "nc-" + Guid.NewGuid().ToString("N")), _producer, _blobs, 150, 1,
            container: Format2TestKit.NotAContainer(what));
        var result = await _store.AdoptAsync(bad, 0, true);

        result.Outcome.Should().Be(AdoptOutcome.Refused, what);
        (await _store.GetCurrentAsync())!.Files.Manifest.BaselineId.Should().Be(first.Manifest.BaselineId, "a refusal changes nothing");
    }

    /// <summary>
    /// Review round: the files behind <c>tbl_baseline</c> are checked against the signed manifest every time the store hands
    /// them out, not only when they arrive. A baseline whose files changed on disk is not served (fail closed), and the same
    /// baseline sent again puts its files back.
    /// </summary>
    [Theory]
    [InlineData("container byte")]
    [InlineData("container shortened")]
    [InlineData("blob list")]
    [InlineData("signature file")]
    [InlineData("manifest file")]
    public async Task ABaselineWhoseFilesChangedOnDisk_IsNotServed_AndTheSameBaselineAgainRepairsIt(string what)
    {
        var files = Baseline();
        (await _store.AdoptAsync(files, 3, true)).Outcome.Should().Be(AdoptOutcome.Adopted);
        (await _store.GetCurrentAsync()).Should().NotBeNull();

        var name = what switch
        {
            "blob list" => BaselineManifest.BlobListFileName,
            "signature file" => BaselineManifest.SignatureFileName,
            "manifest file" => BaselineManifest.FileName,
            _ => BaselineManifest.ContainerFileName,
        };
        var target = Path.Combine(_store.CurrentDirectory, name);
        var original = File.ReadAllBytes(target);
        File.WriteAllBytes(target, what == "container shortened" ? original[..^20] : Flip(original, what == "blob list" ? 3 : original.Length - 3));

        (await _store.GetCurrentAsync()).Should().BeNull($"{what}: the baseline is not the signed one any more");

        (await _store.AdoptAsync(files, 3, true)).Outcome.Should().Be(AdoptOutcome.AlreadyCurrent);
        var repaired = await _store.GetCurrentAsync();
        repaired.Should().NotBeNull($"{what}: the same baseline again put its files back");
        File.ReadAllBytes(target).Should().Equal(original);
    }

    /// <summary>A changed container is found once the check is repeated (the hash is not recomputed on every call, spec 10.1's status is polled).</summary>
    [Fact]
    public async Task AContainerFlipped_WithItsModificationTimePutBack_IsFoundAtTheNextRecheck()
    {
        var clock = new MutableClock();
        var store = new BaselineStore(_node.Factory, _node.DataPath, _policy, time: clock);
        (await store.AdoptAsync(Baseline(), 0, true)).Outcome.Should().Be(AdoptOutcome.Adopted);
        (await store.GetCurrentAsync()).Should().NotBeNull();

        var target = Path.Combine(store.CurrentDirectory, BaselineManifest.ContainerFileName);
        var stamp = File.GetLastWriteTimeUtc(target);
        File.WriteAllBytes(target, Flip(File.ReadAllBytes(target), 100));
        File.SetLastWriteTimeUtc(target, stamp);

        (await store.GetCurrentAsync()).Should().NotBeNull("within the interval the cached check stands");
        clock.Advance(BaselineStore.RecheckInterval + TimeSpan.FromMinutes(1));
        (await store.GetCurrentAsync()).Should().BeNull("after it the hash is computed again");
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Review round, finding 1: the flag that says "legacy content is gone" is set after the file is rebuilt and the WAL
    /// truncated, never before. Here the truncation cannot finish (a reader holds an old snapshot of the WAL): the adoption
    /// fails, the flag is not set, and the next adoption - of the baseline already current - finishes the purge.
    /// </summary>
    [Fact]
    public async Task APurgeThatCannotTruncateTheWal_LeavesNoFlag_AndIsFinishedByTheNextAdoption()
    {
        var canaries = await SeedLegacyContentAsync();
        var files = Baseline();
        using var reader = (SqliteConnection)_node.Factory.CreateConnection();
        var pinned = reader.BeginTransaction(deferred: true);   // a read snapshot of the WAL, no write lock
        try
        {
            await reader.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_folder", transaction: pinned);

            var adopt = () => _store.AdoptAsync(files, 0, true);
            await adopt.Should().ThrowAsync<IOException>("the legacy pages may still be in the WAL; this is not a finished adoption")
                .WithMessage("*write-ahead log*");
            using (var conn = _node.Factory.CreateConnection())
                (await conn.ExecuteScalarAsync<string?>("SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'"))
                    .Should().BeNull("the node must not claim an opaque store before the file is clean");
        }
        finally
        {
            pinned.Dispose();
        }

        (await _store.AdoptAsync(files, 0, true)).Outcome.Should().Be(AdoptOutcome.AlreadyCurrent);
        using (var conn = _node.Factory.CreateConnection())
            (await conn.ExecuteScalarAsync<string>("SELECT value FROM tbl_blind_state WHERE key = 'opaque_store'")).Should().Be("1");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(_node.DataPath, canaries).Should().BeEmpty();
    }

    private static byte[] Flip(byte[] bytes, int at)
    {
        var copy = (byte[])bytes.Clone();
        copy[at] ^= 1;
        return copy;
    }

    private async Task<List<string>> SeedLegacyContentAsync()
    {
        var title = MetadataCanaries.Ascii("title");
        var path = MetadataCanaries.NonAscii("path");
        var folder = MetadataCanaries.Ascii("folder");
        var tag = MetadataCanaries.Ascii("tag");
        var file = MetadataCanaries.Ascii("file");
        var comment = MetadataCanaries.Ascii("comment");
        var now = DateTime.UtcNow.ToString("O");
        var articleId = Guid.NewGuid().ToString();
        using (var conn = _node.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES (@id, @path, @folder, @now, @now)",
                new { id = Guid.NewGuid().ToString(), path = "/" + folder, folder, now });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@articleId, @title, @path, 'A', @now, @now)",
                new { articleId, title, path, now });
            await conn.ExecuteAsync("INSERT INTO tbl_concept_tag (name) VALUES (@tag)", new { tag });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size, encrypted_dek, dek_iv, iv, created_at) VALUES (@id, @articleId, @file, 'image/png', 1, X'00', X'00', X'00', @now)",
                new { id = Guid.NewGuid().ToString(), articleId, file, now });
            await conn.ExecuteAsync("INSERT INTO tbl_comment (article_id, text, created_at) VALUES (@articleId, @comment, @now)",
                new { articleId, comment, now });
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(PASSIVE)");
        }
        var clear = Format2TestKit.ClearArticleEvent(_producer.NodeId, _producer.PrivateKey, 40, title, path);
        await _node.Events.AppendAsync(clear);
        await new SyncQuarantineRepository(_node.Factory).RecordFailureAsync(clear.EventId, clear.EventType, clear.NodeId,
            $"folder {path} not found", BeeMemoryBank.Core.Interfaces.SyncFailureKind.Permanent);
        return [title, path, folder, tag, file, comment];
    }

    private async Task<List<string>> NonOperationalTablesWithRowsAsync()
    {
        using var conn = _node.Factory.CreateConnection();
        var tables = (await conn.QueryAsync<(string Name, string? Sql)>("SELECT name, sql FROM sqlite_master WHERE type = 'table'")).ToList();
        var virtualTables = tables.Where(t => t.Sql?.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) == true).Select(t => t.Name).ToList();
        var withRows = new List<string>();
        foreach (var (name, _) in tables)
        {
            if (BaselineStore.OperationalTables.Contains(name) || name.StartsWith("sqlite_", StringComparison.Ordinal)) continue;
            // A full-text index's shadow tables keep their bookkeeping rows; the index itself is checked through its virtual
            // table, and the byte scan above proves no term of it survived.
            if (virtualTables.Any(v => name.StartsWith(v + "_", StringComparison.Ordinal))) continue;
            if (await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM \"{name}\"") > 0) withRows.Add(name);
        }
        return withRows;
    }

    private async Task SetProducerAsync(string set)
    {
        using var conn = _node.Factory.CreateConnection();
        await conn.ExecuteAsync($"UPDATE tbl_whitelist SET {set} WHERE upper(node_id) = upper(@id)", new { id = _producer.NodeId.ToString() });
    }

    private static byte[] Flip(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[7] ^= 1;
        return copy;
    }

    private static void FlipFile(string path) => File.WriteAllBytes(path, Flip(File.ReadAllBytes(path)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
