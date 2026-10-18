using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BaselineRestoreCollection : ICollectionFixture<BaselineRestoreFixture>
{
    public const string Name = "BaselineRestore";
}

/// <summary>
/// Restore from a baseline and deltas (docs/design/step-a-spec.md 12, 16.4 items 3-5): the round trip across two master-key rotations,
/// a blind node that holds deltas back, a restore cut by a crash and run again, and what is refused before anything is written.
/// </summary>
[Collection(BaselineRestoreCollection.Name)]
public class BaselineRestoreTests(BaselineRestoreFixture src)
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", BaselineRestoreFixture.Password);

    /// <summary>An <see cref="IRestoreSourceV2"/> over the fixture's package that a test can damage.</summary>
    private sealed class DamagedSource(IRestoreSourceV2 inner) : IRestoreSourceV2
    {
        public Func<IEnumerable<SyncEvent>, IEnumerable<SyncEvent>>? Deltas { get; set; }
        public BaselineFiles Baseline => inner.Baseline;
        public BlindManifestV2 Manifest { get; set; } = inner.Manifest;
        public string ControlDbPath => inner.ControlDbPath;
        public IEnumerable<SyncEvent> ReadDeltas() => Deltas is null ? inner.ReadDeltas() : Deltas(inner.ReadDeltas());
        public Stream? OpenBlob(string hash) => inner.OpenBlob(hash);
        public void Dispose() => inner.Dispose();
    }

    private static Task<RestoreResult> RestoreAsync(RecoveryTestFactory target, IRestoreSourceV2 source, RestoreIdentity? who = null) =>
        target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromBaselineAsync(source, who ?? Who, blind: null, extraEvidence: []);

    private IRestoreSourceV2 Source() => new PackageRestoreSource(src.Package);

    private static async Task<bool> HasIdentityAsync(RecoveryTestFactory target)
    {
        using var scope = target.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync() is not null;
    }

    [Fact]
    public async Task Round_trip_across_two_rotations_every_change_arrives_and_the_anchor_confirms_it()
    {
        using var target = new RecoveryTestFactory();

        var result = await RestoreAsync(target, Source());

        result.Replay.Should().NotBeNull();
        result.Replay!.Skipped.Should().Be(0);
        result.Replay.Deferred.Should().Be(0, "nothing the deltas need is missing");
        result.Replay.Total.Should().Be(src.Deltas.Count);
        result.RetiredKeys.Should().Be(2, "both rotations are in the chain");
        result.Anchor.MatchesAnchor.Should().BeTrue("the restored state equals the source's at the anchor's cut: " + await src.DescribeDifferencesAsync(target));
        result.Anchor.Confirmed.Should().BeTrue("the anchor is the last event: nothing is newer than it");
        result.Anchor.State.Should().Be("confirmed");
        src.Deltas.Count(d => d.EventType == EventClasses.Sealed).Should().BeGreaterThan(5, "the deltas of the scenario are sealed");
    }

    [Fact]
    public async Task A_restore_leaves_no_copy_of_the_decrypted_baseline_or_of_its_sidecars()
    {
        using var target = new RecoveryTestFactory();

        await RestoreAsync(target, Source());

        StagingEntries(target.DataPath).Should().BeEmpty("the decrypted baseline database is removed once the restore has used it");
    }

    [Fact]
    public async Task A_restore_whose_decrypted_copy_cannot_be_removed_is_not_reported_as_done()
    {
        using var target = new RecoveryTestFactory();
        var staging = SnapshotStaging.DirIn(target.DataPath);
        // A folder under a sidecar's name stands for a sidecar that cannot be deleted, on every platform. Only this target's copy gets one: the seam is static.
        BaselineRestoreRun.AfterStagingWork = copy =>
        {
            if (copy.StartsWith(staging, StringComparison.Ordinal)) Directory.CreateDirectory(copy + "-journal");
        };
        try
        {
            var act = () => RestoreAsync(target, Source());

            await act.Should().ThrowAsync<IOException>("a copy that stays makes the restore repair-required, never done");
        }
        finally
        {
            BaselineRestoreRun.AfterStagingWork = null;
            foreach (var dir in StagingEntries(target.DataPath).Where(Directory.Exists)) Directory.Delete(dir, recursive: true);
        }
    }

    private static List<string> StagingEntries(string dataPath) =>
        Directory.Exists(SnapshotStaging.DirIn(dataPath))
            ? Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(dataPath)).ToList()
            : [];

    [Fact]
    public async Task The_restored_vault_opens_and_holds_what_the_source_holds()
    {
        using var target = new RecoveryTestFactory();
        await RestoreAsync(target, Source());
        await target.Services.GetRequiredService<SessionService>().UnlockAsync(BaselineRestoreFixture.Password);

        using var scope = target.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        using var targetDb = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        using var sourceDb = src.Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();

        // every live note: same title, path, status and the same body once opened (under whichever master key it sits)
        var sourceRows = (await sourceDb.QueryAsync<(string Id, string Title, string Path, string Status)>(
            "SELECT id, title, tree_path, status FROM tbl_article ORDER BY id")).ToList();
        var targetRows = (await targetDb.QueryAsync<(string Id, string Title, string Path, string Status)>(
            "SELECT id, title, tree_path, status FROM tbl_article ORDER BY id")).ToList();
        targetRows.Should().Equal(sourceRows);
        foreach (var (id, _, _, status) in sourceRows.Where(r => r.Status == "A"))
            (await articles.GetContentAsync(Guid.Parse(id))).Should().Be(src.Bodies[Guid.Parse(id)]);

        (await targetDb.QueryAsync<string>("SELECT path FROM tbl_folder WHERE status = 'A' ORDER BY path")).Should()
            .Equal(await sourceDb.QueryAsync<string>("SELECT path FROM tbl_folder WHERE status = 'A' ORDER BY path"));
        (await targetDb.QueryAsync<string>("SELECT name FROM tbl_concept_tag ORDER BY name")).Should()
            .Equal(await sourceDb.QueryAsync<string>("SELECT name FROM tbl_concept_tag ORDER BY name"));
        (await targetDb.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_comment")).Should()
            .Be(await sourceDb.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_comment"));
        (await targetDb.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_media WHERE status = 'A'")).Should()
            .Be(await sourceDb.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_media WHERE status = 'A'"));
        (await targetDb.QueryAsync<string>("SELECT hash FROM tbl_blob ORDER BY hash")).Should()
            .Equal(await sourceDb.QueryAsync<string>("SELECT hash FROM tbl_blob ORDER BY hash"));
    }

    [Fact]
    public async Task The_note_hard_deleted_after_the_baseline_stays_deleted_and_its_audit_is_kept()
    {
        using var target = new RecoveryTestFactory();
        await RestoreAsync(target, Source());

        using var db = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE id = @id COLLATE NOCASE", new { id = src.HardDeletedArticle.ToString() }))
            .Should().Be(0, "the baseline holds it and the replayed hard delete removes it");
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_hard_delete_audit")).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_restored_node_is_activated_and_holds_both_metadata_keys_and_its_events()
    {
        using var target = new RecoveryTestFactory();
        await RestoreAsync(target, Source());

        using var db = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await db.QueryAsync<string>("SELECT key_id FROM tbl_metadata_key ORDER BY generation"))
            .Should().Equal(src.FirstKeyId, src.SecondKeyId);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_activation")).Should().Be(1);
        var stored = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event");
        stored.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(src.Deltas.Count);
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_type = 'sealed'")).Should().BeGreaterThan(0,
            "the deltas are stored as received: the original sealed bytes");
    }

    [Fact]
    public async Task Pull_positions_come_from_the_signed_baseline_and_the_baselines_producer_is_at_its_log_head()
    {
        using var target = new RecoveryTestFactory();
        await RestoreAsync(target, Source());

        using var scope = target.Services.CreateScope();
        var position = await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(src.SourceNodeId);
        position!.LastSequenceNum.Should().Be(src.Baseline.Manifest.ProducerLogHead);
    }

    [Fact]
    public async Task A_blind_node_that_holds_a_delta_back_is_caught_by_the_anchor()
    {
        using var target = new RecoveryTestFactory();
        // the edit of the first note, which the final anchor covers
        var withheld = src.Deltas.First(d => d.EventType == EventClasses.Sealed && d.SequenceNum > src.Baseline.Manifest.ProducerLogHead + 2);
        var source = new DamagedSource(Source()) { Deltas = all => all.Where(d => d.EventId != withheld.EventId) };

        var result = await RestoreAsync(target, source);

        result.Replay!.Total.Should().Be(src.Deltas.Count - 1);
        result.Anchor.Found.Should().BeTrue("the anchor event itself was delivered");
        result.Anchor.MatchesAnchor.Should().BeFalse("the restored state lacks a change the anchor covers");
        result.Anchor.Confirmed.Should().BeFalse();
        result.Anchor.State.Should().Be("unconfirmed");
    }

    [Fact]
    public async Task A_forged_delta_is_skipped_and_never_applied()
    {
        using var target = new RecoveryTestFactory();
        var forged = src.Deltas.Last(d => d.EventType == EventClasses.Sealed);
        var source = new DamagedSource(Source())
        {
            Deltas = all => all.Select(d =>
            {
                if (d.EventId != forged.EventId) return d;
                var copy = new SyncEvent
                {
                    SequenceNum = d.SequenceNum, EventId = d.EventId, NodeId = d.NodeId, LamportTs = d.LamportTs, EventType = d.EventType,
                    ArticleId = d.ArticleId, EntityId = d.EntityId, Payload = d.Payload, ProtocolVersion = d.ProtocolVersion, CreatedAt = d.CreatedAt,
                    Signature = d.Signature.ToArray(),
                };
                copy.Signature[0] ^= 0xFF;
                return copy;
            })
        };

        var result = await RestoreAsync(target, source);

        result.Replay!.Skipped.Should().Be(1);
        using var db = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_id = @id COLLATE NOCASE", new { id = forged.EventId.ToString() })).Should().Be(0);
        result.Anchor.Confirmed.Should().BeFalse("the forged change is missing from the state");
    }

    [Fact]
    public async Task A_restore_cut_by_a_crash_is_run_again_and_finishes_with_equal_tables_and_no_resurrection()
    {
        using var target = new RecoveryTestFactory();
        var restore = target.Services.GetRequiredService<RecoveryRestoreService>();
        var previous = BaselineRestoreRun.MarkerEvery;
        BaselineRestoreRun.MarkerEvery = 2;
        try
        {
            var cutAfter = Math.Max(4, src.Deltas.Count / 2);
            restore.StepForTests = step => step == $"replayed:{cutAfter}" ? throw new IOException("the power went") : Task.CompletedTask;
            var act = () => restore.RestoreFromBaselineAsync(Source(), Who, null, []);
            await act.Should().ThrowAsync<IOException>();
            (await HasIdentityAsync(target)).Should().BeTrue("the bootstrap had written the identity");
            using (var db = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
                (await db.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = 'restore_replay_seq'")).Should().NotBeNull();

            // The engine's own resume is under test here, not the node's: the cut restore's seal intent is out of the picture (a node's next start would wipe what
            // the cut wrote, and the endpoint takes it back at once; a second begin over a pending intent is refused on purpose - SealIntentHostTests).
            SealIntent.Complete(target.DataPath);
            restore.StepForTests = null;
            var result = await restore.RestoreFromBaselineAsync(Source(), Who, null, []);

            result.Replay!.Resumed.Should().BeTrue("the replay continued after the last marked event");
            result.Replay.Skipped.Should().Be(0);
            result.Anchor.Confirmed.Should().BeTrue();
            using var check = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
            (await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE id = @id COLLATE NOCASE", new { id = src.HardDeletedArticle.ToString() }))
                .Should().Be(0, "the baseline was not imported over the replay again");
            (await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key IN ('restore_replay_seq', 'restore_baseline_imported')"))
                .Should().Be(0, "the markers come down with the restore");
        }
        finally
        {
            BaselineRestoreRun.MarkerEvery = previous;
        }
    }

    // ---- refusals: nothing is written ----

    [Fact]
    public async Task A_wrong_master_password_opens_nothing_and_writes_nothing()
    {
        using var target = new RecoveryTestFactory();

        var act = () => RestoreAsync(target, Source(), Who with { Password = "WrongPassword9" });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_baseline_whose_key_no_recovered_key_opens_is_refused_before_anything_is_written()
    {
        var package = src.ClonePackage("nokey");
        using (var control = new SqliteConnection($"Data Source={package.ControlDbPath};Pooling=False"))
        {
            control.Open();
            await control.ExecuteAsync("DELETE FROM tbl_metadata_key WHERE key_id = @k", new { k = src.FirstKeyId });
        }
        using var target = new RecoveryTestFactory();

        var act = () => RestoreAsync(target, new PackageRestoreSource(package));

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("metadata key");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_damaged_baseline_container_is_refused_before_anything_is_written()
    {
        var package = src.ClonePackage("damaged");
        var container = Path.Combine(package.Baseline.Directory, "baseline.bmbch");
        var bytes = await File.ReadAllBytesAsync(container);
        bytes[bytes.Length / 2] ^= 0x10;
        await File.WriteAllBytesAsync(container, bytes);
        using var target = new RecoveryTestFactory();

        var act = () => RestoreAsync(target, new PackageRestoreSource(package));

        await act.Should().ThrowAsync<InvalidDataException>();
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_baseline_produced_by_a_node_that_is_not_a_superadmin_in_the_package_is_refused()
    {
        var manifest = src.Package.Manifest with
        {
            Whitelist = [.. src.Package.Manifest.Whitelist.Select(p => p with { IsSuperadmin = false })]
        };
        using var target = new RecoveryTestFactory();

        var act = () => RestoreAsync(target, new DamagedSource(Source()) { Manifest = manifest });

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("superadmin");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_listed_blob_the_source_does_not_hold_stops_the_restore()
    {
        var package = src.ClonePackage("noblob");
        var first = (await File.ReadAllLinesAsync(Path.Combine(package.Baseline.Directory, "blobs.txt"))).First(l => l.Length > 0);
        File.Delete(Path.Combine(package.BlobsDirectory!, first));
        using var target = new RecoveryTestFactory();

        var act = () => RestoreAsync(target, new PackageRestoreSource(package));

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("blob");
    }

    [Fact]
    public async Task A_blob_that_does_not_hash_to_its_name_is_refused()
    {
        var package = src.ClonePackage("badblob");
        var first = (await File.ReadAllLinesAsync(Path.Combine(package.Baseline.Directory, "blobs.txt"))).First(l => l.Length > 0);
        await File.WriteAllBytesAsync(Path.Combine(package.BlobsDirectory!, first), [1, 2, 3]);
        using var target = new RecoveryTestFactory();

        var act = () => RestoreAsync(target, new PackageRestoreSource(package));

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("hash");
    }
}
