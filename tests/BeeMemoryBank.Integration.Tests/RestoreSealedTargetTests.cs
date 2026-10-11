using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindPhone;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Every restore consumer of docs/design/step-b-spec.md 8.4 run onto an ENCRYPTED fresh node (red test 15.1.9): the restore from a format-2 package, from the
/// archive a blind node serves, and from an Android backup file, with the canary vault of the Step A baseline fixture. The target is a real host whose
/// vault factory is keyed (the key source is registered, so the composition root builds a sealed <see cref="DbConnectionFactory"/>); the gate of track 3 is
/// not on this branch. The staging folder is watched from its own thread: no plain database may exist in it at any moment, and nothing in the data
/// directory holds a canary in clear afterwards. The plain control restore proves the scan can see them.
/// </summary>
[Collection(BaselineRestoreCollection.Name)]
public sealed class RestoreSealedTargetTests(BaselineRestoreFixture src)
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", BaselineRestoreFixture.Password);

    private static RecoveryTestFactory SealedTarget(InMemoryVaultKeySource keys) =>
        new(services: s => s.AddSingleton<IVaultKeySource>(keys));

    private static async Task AssertRestoredLikeTheSourceAsync(RecoveryTestFactory target, BaselineRestoreFixture src, RestoreResult result)
    {
        result.Replay.Should().NotBeNull();
        result.Replay!.Skipped.Should().Be(0);
        result.Replay.Deferred.Should().Be(0);
        result.Anchor.MatchesAnchor.Should().BeTrue("the restored state equals the source's at the anchor's cut");
        result.Anchor.Confirmed.Should().BeTrue();
        using var db = target.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        using var sourceDb = src.Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await db.QueryAsync<(string Id, string Title, string Path, string Status)>("SELECT id, title, tree_path, status FROM tbl_article ORDER BY id"))
            .Should().Equal(await sourceDb.QueryAsync<(string Id, string Title, string Path, string Status)>("SELECT id, title, tree_path, status FROM tbl_article ORDER BY id"));
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article")).Should().BeGreaterThan(0);
    }

    private static string LiveDb(RecoveryTestFactory target) => Path.Combine(target.DataPath, "beememorybank.db");

    private void AssertNoPlainFile(PlainFileWatch watch, RecoveryTestFactory target)
    {
        watch.Seen.Keys.Should().NotBeEmpty("the watch saw the staging folder at work");
        watch.Plain.Should().BeEmpty("no plain database exists in the staging folder at any moment");
        watch.CanaryHits.Should().BeEmpty();
        var staging = SnapshotStaging.DirIn(target.DataPath);
        (!Directory.Exists(staging) || !Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Any(f => !f.EndsWith("-journal"))).Should().BeTrue("the staging copy is gone");
        VaultStoreFiles.HasSqliteMagic(LiveDb(target)).Should().BeFalse("the node's database is still sealed");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(target.DataPath, src.Canaries).Should().BeEmpty("nothing in the data directory holds a canary in clear");
    }

    [Fact]
    public async Task The_control_a_plain_target_restored_from_the_same_package_holds_the_canaries_in_clear()
    {
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<RecoveryRestoreService>()
            .RestoreFromBaselineAsync(new PackageRestoreSource(src.Package), Who, blind: null, extraEvidence: []);

        result.Replay.Should().NotBeNull();
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(target.DataPath, src.Canaries).Should().NotBeEmpty("a plain node's database holds the titles in clear");
    }

    [Fact]
    public async Task A_format2_package_restores_onto_an_encrypted_node_through_the_importer_with_no_plain_file()
    {
        var keys = new InMemoryVaultKeySource();
        using var target = SealedTarget(keys);
        _ = target.Services;
        Directory.CreateDirectory(SnapshotStaging.DirIn(target.DataPath));
        using var watch = new PlainFileWatch([SnapshotStaging.DirIn(target.DataPath)], src.Canaries);

        var result = await target.Services.GetRequiredService<RecoveryRestoreService>()
            .RestoreFromBaselineAsync(new PackageRestoreSource(src.Package), Who, blind: null, extraEvidence: []);
        watch.Stop();

        await AssertRestoredLikeTheSourceAsync(target, src, result);
        AssertNoPlainFile(watch, target);
        target.Services.GetRequiredService<DbConnectionFactory>().IsSealed.Should().BeTrue();
    }

    [Fact]
    public async Task The_signed_archive_a_blind_node_serves_restores_onto_an_encrypted_node_with_no_plain_file()
    {
        var keys = new InMemoryVaultKeySource();
        using var target = SealedTarget(keys);
        _ = target.Services;
        Directory.CreateDirectory(SnapshotStaging.DirIn(target.DataPath));
        using var watch = new PlainFileWatch([SnapshotStaging.DirIn(target.DataPath)], src.Canaries);
        var blind = new RestoreBlindPeer(src.ServingNodeId, "Blind node", src.ServingPublicKey, "https://blind.test:5610", 0, null);

        var result = await target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromBlindPackageAsync(
            src.ArchivePath, src.ArchiveSignature, src.ServingPublicKey, Who, blind, [], CancellationToken.None);
        watch.Stop();

        await AssertRestoredLikeTheSourceAsync(target, src, result);
        AssertNoPlainFile(watch, target);
    }

    [Fact]
    public async Task An_android_backup_restores_onto_an_encrypted_node_with_no_plain_file()
    {
        var file = await new BaselineRestoreSourcesTests(src).AndroidBackupAsync();
        var keys = new InMemoryVaultKeySource();
        using var target = SealedTarget(keys);
        _ = target.Services;
        Directory.CreateDirectory(SnapshotStaging.DirIn(target.DataPath));
        using var watch = new PlainFileWatch([SnapshotStaging.DirIn(target.DataPath)], src.Canaries);

        var result = await target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single()
            .RestoreAsync(file, Who, CancellationToken.None, RestoreBoxPolicy.Default);
        watch.Stop();

        await AssertRestoredLikeTheSourceAsync(target, src, result);
        AssertNoPlainFile(watch, target);
    }
}
