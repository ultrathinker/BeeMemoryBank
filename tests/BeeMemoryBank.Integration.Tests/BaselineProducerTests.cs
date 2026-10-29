using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>The baseline producer of a full node (docs/design/step-a-spec.md 7.2, 7.3, 16.4 item 2): what it contains and what it refuses.</summary>
[Collection(HeavyOperationCollection.Name)]
public class BaselineProducerTests : IAsyncLifetime
{
    private StepAFactory _node = null!;
    private StepANode.SeededVault _vault = null!;
    private string _keyId = null!;
    private byte[] _mek = null!;
    private string _work = null!;

    public async Task InitializeAsync()
    {
        _node = await StepANode.CreateAsync();
        _vault = await StepANode.SeedWithCanariesAsync(_node);
        (_keyId, _mek) = await StepANode.ActivateAsync(_node);
        _work = Path.Combine(_node.DataPath, "test-work");
        Directory.CreateDirectory(_work);
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        return Task.CompletedTask;
    }

    private async Task<BaselineFiles> ProduceAsync(Action<BaselineProducer>? configure = null)
    {
        using var scope = _node.Services.CreateScope();
        var producer = scope.ServiceProvider.GetRequiredService<BaselineProducer>();
        configure?.Invoke(producer);
        return await producer.ProduceAsync(BaselineReasons.OnDemand);
    }

    private async Task<string> OpenAsync(BaselineFiles files)
    {
        var path = Path.Combine(_work, $"open-{Guid.NewGuid():N}.db");
        await StepANode.OpenBaselineAsync(files.Directory, _keyId, _mek, path);
        return path;
    }

    private string[] ProducedDirectories() =>
        Directory.Exists(ProducedBaselines.Root(_node.DataPath)) ? Directory.GetDirectories(ProducedBaselines.Root(_node.DataPath)) : [];

    private static SqliteConnection Open(string db) => new($"Data Source={db};Mode=ReadOnly;Pooling=False");

    [Fact]
    public async Task Container_and_manifest_hold_no_canary_while_the_sealed_database_does()
    {
        var files = await ProduceAsync();

        MetadataCanaries.ScanDirectory(files.Directory, _vault.Canaries).Should().BeEmpty("nothing in the baseline directory shows a title, a path or a tag");
        MetadataCanaries.Scan(files.ManifestBytes, _vault.Canaries, "manifest").Should().BeEmpty();
        // Control: the scanner finds every canary in the same artefact once it is opened, so the empty result above means something.
        var plain = await OpenAsync(files);
        var hits = MetadataCanaries.ScanFile(plain, _vault.Canaries);
        hits.Select(h => h.Canary).Distinct().Should().BeEquivalentTo(_vault.Canaries, "every canary is in the opened database, so the scanner can see what it looks for");
        File.ReadAllBytes(Path.Combine(files.Directory, "baseline.bmbch")).AsSpan(0, 8).SequenceEqual("BMBCH1\0\0"u8).Should().BeTrue();
    }

    [Fact]
    public async Task Database_has_the_replicated_content_without_secrets_blobs_or_derived_columns()
    {
        using (var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = X'0102030405060708', embedding_model_version = 'm1'");
            await conn.ExecuteAsync("UPDATE tbl_concept_tag SET embedding = X'0A0B0C', embedding_model_version = 'm1'");
            await conn.ExecuteAsync(
                "INSERT INTO tbl_hard_delete_audit (occurred_at, entity_type, entity_identifier) VALUES (@at, 'article', 'gone-for-good')",
                new { at = DateTime.UtcNow.ToString("O") });
        }
        long liveArticles;
        using (var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            liveArticles = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article");

        var files = await ProduceAsync();
        using var db = Open(await OpenAsync(files));

        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article")).Should().Be(liveArticles);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article_body")).Should().BeGreaterThan(0);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE embedding_projection IS NOT NULL OR embedding_model_version IS NOT NULL")).Should().Be(0);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_concept_tag WHERE embedding IS NOT NULL OR embedding_model_version IS NOT NULL")).Should().Be(0);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blob")).Should().Be(0, "blobs travel apart");
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_hard_delete_audit")).Should().Be(1);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_key")).Should().Be(1);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_activation")).Should().Be(1);
        foreach (var gone in new[] { "tbl_key_slot", "tbl_event", "tbl_node_identity", "tbl_session", "tbl_user", "tbl_sync_position" })
            (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = @gone", new { gone })).Should().Be(0, gone);
    }

    [Fact]
    public async Task Sealed_database_is_one_clean_file_that_holds_no_trace_of_what_was_nulled_or_emptied()
    {
        var sentinel = Encoding.ASCII.GetBytes("BMB-BASELINE-PROJECTION-SENTINEL-77ac01-do-not-ship");
        var projection = Enumerable.Repeat(sentinel, 200).SelectMany(b => b).ToArray();
        using (var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_article SET embedding_projection = @p", new { p = projection });

        var files = await ProduceAsync();
        var plain = await OpenAsync(files);

        File.ReadAllBytes(plain).AsSpan().IndexOf(sentinel).Should().Be(-1, "a nulled value must not survive in a free page");
        Directory.GetFiles(_work, Path.GetFileName(plain) + "*").Should().ContainSingle("no journal or wal beside the opened copy");
    }

    [Fact]
    public async Task Blob_list_equals_the_references_and_the_manifest_describes_the_files()
    {
        var files = await ProduceAsync();
        var manifest = files.Manifest;
        var listBytes = File.ReadAllBytes(Path.Combine(files.Directory, "blobs.txt"));
        var plain = await OpenAsync(files);
        using var db = Open(plain);
        var expected = (await db.QueryAsync<string>(
                @"SELECT ciphertext_hash FROM tbl_article_body WHERE ciphertext_hash IS NOT NULL
                  UNION SELECT ciphertext_hash FROM tbl_article_version WHERE ciphertext_hash IS NOT NULL
                  UNION SELECT ciphertext_sha256 FROM tbl_media WHERE ciphertext_sha256 IS NOT NULL"))
            .Distinct().Order(StringComparer.Ordinal).ToList();

        expected.Should().NotBeEmpty("the seeded notes have bodies");
        Encoding.ASCII.GetString(listBytes).Should().Be(string.Concat(expected.Select(h => h + "\n")));
        manifest.BlobCount.Should().Be(expected.Count);
        manifest.BlobListSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(listBytes)));
        using var live = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        manifest.BlobBytes.Should().Be(await live.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(size), 0) FROM tbl_blob WHERE hash IN @expected", new { expected }));

        var containerPath = Path.Combine(files.Directory, "baseline.bmbch");
        manifest.ContainerBytes.Should().Be(new FileInfo(containerPath).Length);
        manifest.ContainerSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(containerPath))));
    }

    [Fact]
    public async Task Manifest_is_signed_by_the_node_and_names_the_cut_the_key_and_the_schema()
    {
        var self = (await _node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        long head;
        using (var scope = _node.Services.CreateScope())
            head = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();

        var files = await ProduceAsync();
        var manifest = files.Manifest;

        Ed25519Signer.Verify(self.Ed25519PublicKey, BaselineManifest.SignaturePayload(files.ManifestBytes), files.Signature).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(files.Directory, "manifest.json")).Should().Equal(files.ManifestBytes);
        File.ReadAllBytes(Path.Combine(files.Directory, "manifest.json.sig")).Should().Equal(files.Signature);
        manifest.Format.Should().Be("bmb-baseline-v1");
        manifest.ProducerNodeId.Should().Be(self.NodeId);
        manifest.KeyId.Should().Be(_keyId);
        manifest.KeyGeneration.Should().Be(1);
        manifest.ProducerLogHead.Should().BeGreaterThanOrEqualTo(head).And.BeLessThanOrEqualTo(head + 5);
        manifest.LamportAtCut.Should().BeGreaterThan(0);
        manifest.SchemaVersion.Should().BeGreaterThanOrEqualTo(39);
        manifest.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        var tampered = files.ManifestBytes.ToArray();
        tampered[^3] ^= 1;
        Ed25519Signer.Verify(self.Ed25519PublicKey, BaselineManifest.SignaturePayload(tampered), files.Signature).Should().BeFalse();
        // domain separation: the signature is not a signature over the bare manifest
        Ed25519Signer.Verify(self.Ed25519PublicKey, files.ManifestBytes, files.Signature).Should().BeFalse();
    }

    [Fact]
    public async Task Files_are_owner_only_and_no_staging_file_is_left()
    {
        var files = await ProduceAsync();

        foreach (var file in Directory.GetFiles(files.Directory))
            OwnerOnlyFile.IsOwnerOnly(file).Should().BeTrue(file);
        var staging = SnapshotStaging.DirIn(_node.DataPath);
        (Directory.Exists(staging) ? Directory.GetFiles(staging) : []).Should().BeEmpty("the plaintext copy and the self-check copy are deleted");
    }

    [Fact]
    public async Task A_baseline_leaves_none_of_the_sidecars_of_its_staging_copies()
    {
        var files = await ProduceAsync(p => p.AfterStagingWork = PlantClearSidecars);

        files.Should().NotBeNull();
        StagingEntries().Should().BeEmpty("the plaintext copy, the self-check copy and each sidecar of them are deleted");
    }

    [Fact]
    public async Task A_baseline_whose_staging_sidecar_cannot_be_removed_is_not_produced()
    {
        // A folder under the name of a sidecar stands for a sidecar that cannot be deleted, on every platform.
        var act = () => ProduceAsync(p => p.AfterStagingWork = copy => Directory.CreateDirectory(copy + "-journal"));

        await act.Should().ThrowAsync<IOException>("a clear copy that stays is repair-required, never a baseline reported as made");
        foreach (var dir in StagingEntries().Where(Directory.Exists)) Directory.Delete(dir, recursive: true);
    }

    private static void PlantClearSidecars(string copy)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) File.WriteAllText(copy + suffix, "clear vault pages");
    }

    private List<string> StagingEntries() =>
        Directory.Exists(SnapshotStaging.DirIn(_node.DataPath))
            ? Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(_node.DataPath)).ToList()
            : [];

    [Fact]
    public async Task Is_refused_before_activation()
    {
        using var plain = await StepANode.CreateAsync("Plain");
        using var scope = plain.Services.CreateScope();
        var refusal = await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<BaselineProducer>().ProduceAsync(BaselineReasons.OnDemand))
            .Should().ThrowAsync<BaselineRefusedException>();
        refusal.Which.Reason.Should().Be(BaselineRefusal.NotActivated);
        Directory.Exists(ProducedBaselines.Root(plain.DataPath)).Should().BeFalse();
    }

    [Fact]
    public async Task Is_refused_when_the_vault_is_locked()
    {
        _node.Services.GetRequiredService<SessionService>().Lock();

        var refusal = await FluentActions.Awaiting(() => ProduceAsync()).Should().ThrowAsync<BaselineRefusedException>();

        refusal.Which.Reason.Should().Be(BaselineRefusal.Locked);
        ProducedDirectories().Should().BeEmpty();
    }

    /// <summary>
    /// Cross-track (integration round): after a DEK rotation the new metadata key exists on this node before its metadata_key_set event does
    /// (spec 4.4, the marker of the two-step rotation), and the key ring already offers it as current. A baseline sealed under it would name a key
    /// no copy holds a row for yet, so the producer waits for the publication, which asks for the early baseline itself.
    /// </summary>
    [Fact]
    public async Task Is_refused_while_the_new_metadata_key_of_a_rotation_is_unpublished_and_works_once_it_is()
    {
        using (var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@Key, 'ffffffffffffffffffffffffffffffff', 'now')",
                new { Key = MetadataKeyPublishMarker.Key });

        var refusal = await FluentActions.Awaiting(() => ProduceAsync()).Should().ThrowAsync<BaselineRefusedException>();

        refusal.Which.Reason.Should().Be(BaselineRefusal.NoMetadataKey, "the scheduler keeps the request and asks again at once for this reason");
        refusal.Which.Message.Should().Contain("not published");
        ProducedDirectories().Should().BeEmpty();

        using (var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @Key", new { Key = MetadataKeyPublishMarker.Key });
        (await ProduceAsync()).Manifest.Should().NotBeNull();
    }

    [Fact]
    public async Task Is_refused_while_received_events_are_held_and_works_once_they_are_applied()
    {
        _node.Held.Held = true;
        var refusal = await FluentActions.Awaiting(() => ProduceAsync()).Should().ThrowAsync<BaselineRefusedException>();
        refusal.Which.Reason.Should().Be(BaselineRefusal.EventsHeld);
        refusal.Which.Message.Should().Contain("Unlock").And.Contain("wait");

        _node.Held.Held = false;
        (await ProduceAsync()).Manifest.Should().NotBeNull();
    }

    /// <summary>
    /// A long local operation (an import, a copy) holds its write admission for its whole run. The cut must not wait for it with the gate
    /// closed: that would hold off every other local write for as long. It gives up after a short wait, says "a write is running", gives
    /// the gate back, and works once the operation is over.
    /// </summary>
    [Fact]
    public async Task Is_refused_after_a_short_wait_while_a_long_write_is_in_flight_and_does_not_keep_the_other_writes_waiting()
    {
        using var logging = _node.Services.CreateScope();
        var longWrite = await logging.ServiceProvider.GetRequiredService<IEventLogger>().BeginAuthoringAsync();
        try
        {
            var started = DateTime.UtcNow;
            var refusal = await FluentActions.Awaiting(() => ProduceAsync(p => p.QuiesceWait = TimeSpan.FromMilliseconds(300)))
                .Should().ThrowAsync<BaselineRefusedException>();

            refusal.Which.Reason.Should().Be(BaselineRefusal.EventsHeld, "the scheduler keeps the request and asks again at the next tick");
            refusal.Which.Message.Should().Contain("write is still running");
            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30), "the wait is bounded");
            ProducedDirectories().Should().BeEmpty("a refused run leaves no half baseline");
            EventWriteGate.Instance.IsQuiescing.Should().BeFalse("the cut that gave up has let the gate go");

            // Another flow's local write goes in at once: the timed-out cut no longer holds _cutover.
            using var other = await EventWriteGate.Instance.EnterAdmissionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            longWrite.Dispose();
        }

        (await ProduceAsync()).Manifest.Should().NotBeNull("with the long write over, the cut goes through");
    }

    [Fact]
    public async Task Is_refused_when_a_blob_the_notes_refer_to_is_missing_here()
    {
        using (var conn = _node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("DELETE FROM tbl_blob WHERE hash = (SELECT ciphertext_hash FROM tbl_article_body LIMIT 1)");

        var refusal = await FluentActions.Awaiting(() => ProduceAsync()).Should().ThrowAsync<BaselineRefusedException>();

        refusal.Which.Reason.Should().Be(BaselineRefusal.BlobMissing);
        ProducedDirectories().Should().BeEmpty("a refused run leaves no half baseline");
    }

    [Fact]
    public async Task Self_check_catches_a_corrupted_container_and_nothing_is_kept()
    {
        var refusal = await FluentActions.Awaiting(() => ProduceAsync(p => p.AfterContainerWritten = path =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                file.Seek(-20, SeekOrigin.End);
                var b = file.ReadByte();
                file.Seek(-20, SeekOrigin.End);
                file.WriteByte((byte)(b ^ 0xFF));
            }))
            .Should().ThrowAsync<BaselineRefusedException>();

        refusal.Which.Reason.Should().Be(BaselineRefusal.SelfCheckFailed);
        ProducedDirectories().Should().BeEmpty();
        var source = _node.Services.GetRequiredService<IBaselineSource>();
        (await source.GetServableAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Large_database_is_sealed_in_several_chunks_and_opens()
    {
        // 4 KiB chunks make even this small database a many-chunk container.
        var files = await ProduceAsync(p => p.ChunkSize = ChunkedContainer.MinChunkSize);

        new FileInfo(Path.Combine(files.Directory, "baseline.bmbch")).Length.Should().BeGreaterThan(4 * ChunkedContainer.MinChunkSize);
        using var db = Open(await OpenAsync(files));
        (await db.ExecuteScalarAsync<string>("PRAGMA integrity_check")).Should().Be("ok");
    }

    [Fact]
    public async Task Only_the_two_newest_produced_baselines_are_kept()
    {
        var first = await ProduceAsync();
        await Task.Delay(30);
        var second = await ProduceAsync();
        await Task.Delay(30);
        var third = await ProduceAsync();

        var kept = ProducedDirectories().Select(Path.GetFileName).ToList();
        kept.Should().BeEquivalentTo(new[] { second.Manifest.BaselineId.ToString("N"), third.Manifest.BaselineId.ToString("N") });
        Directory.Exists(first.Directory).Should().BeFalse();
    }

    [Fact]
    public async Task Full_node_source_serves_the_newest_complete_baseline_with_its_own_log_position_as_the_cut()
    {
        var source = _node.Services.GetRequiredService<IBaselineSource>();
        (await source.GetServableAsync()).Should().BeNull("nothing was produced yet");

        var older = await ProduceAsync();
        await Task.Delay(30);
        var newer = await ProduceAsync();

        var servable = await source.GetServableAsync();
        servable.Should().NotBeNull();
        servable!.Files.Manifest.BaselineId.Should().Be(newer.Manifest.BaselineId);
        servable.CutInLocalLog.Should().Be(newer.Manifest.ProducerLogHead);
        servable.Files.ManifestBytes.Should().Equal(newer.ManifestBytes);
        servable.Files.Signature.Should().Equal(newer.Signature);
        older.Manifest.BaselineId.Should().NotBe(newer.Manifest.BaselineId);
    }

    [Fact]
    public async Task Full_node_source_ignores_a_directory_without_a_signature_and_a_cut_short_container()
    {
        var good = await ProduceAsync();
        await Task.Delay(30);
        var later = await ProduceAsync();
        var source = _node.Services.GetRequiredService<IBaselineSource>();

        // a newer, unsigned directory is not a baseline
        var unsigned = ProducedBaselines.DirectoryOf(_node.DataPath, Guid.NewGuid());
        Directory.CreateDirectory(unsigned);
        File.WriteAllBytes(Path.Combine(unsigned, "baseline.bmbch"), [1, 2, 3]);
        (await source.GetServableAsync())!.Files.Manifest.BaselineId.Should().Be(later.Manifest.BaselineId);

        // a container cut short on disk is not served; the older complete one is
        using (var file = new FileStream(Path.Combine(later.Directory, "baseline.bmbch"), FileMode.Open, FileAccess.Write))
            file.SetLength(file.Length - 1);
        (await source.GetServableAsync())!.Files.Manifest.BaselineId.Should().Be(good.Manifest.BaselineId);
    }

    [Fact]
    public async Task Concurrent_requests_through_the_runner_run_one_at_a_time_and_both_succeed()
    {
        var runner = _node.Services.GetRequiredService<IBaselineProducer>();

        var both = await Task.WhenAll(runner.ProduceAsync("a"), runner.ProduceAsync("b"));

        both.Select(f => f.Manifest.BaselineId).Distinct().Should().HaveCount(2);
        ProducedDirectories().Should().HaveCount(2);
    }
}
