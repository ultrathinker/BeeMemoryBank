using System.Buffers.Text;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindPhone;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The sources a restore from a baseline reads (docs/design/step-a-spec.md 12, 16.4 items 3-5), through their own readers: the signed format-2
/// archive a blind node serves (the restore code's route), an Android blind backup file that holds one, and a Docker blind node's backup folder.
/// What each refuses is checked before anything is written to the new node.
/// </summary>
[Collection(BaselineRestoreCollection.Name)]
public class BaselineRestoreSourcesTests(BaselineRestoreFixture src) : IDisposable
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", BaselineRestoreFixture.Password);
    private const string BlindAddress = "https://blind.test:5610";
    private static readonly string BlindPin = Base64Url.EncodeToString(SHA256.HashData("blind"u8));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_baseline_sources_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // The restore tests' own temp folders are listed in the report; a test's scratch folder is left for the owner to remove.
    }

    private RestoreBlindPeer Blind(Guid? node = null, long cpSeq = 0) =>
        new(node ?? src.ServingNodeId, "Blind node", src.ServingPublicKey, BlindAddress, cpSeq, BlindPin);

    private Task<RestoreResult> RestoreArchiveAsync(RecoveryTestFactory target, string archive, byte[]? signature = null, byte[]? servingKey = null,
        RestoreBlindPeer? blind = null, IReadOnlyList<SyncEvent>? events = null) =>
        target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromBlindPackageAsync(
            archive, signature ?? src.ArchiveSignature, servingKey ?? src.ServingPublicKey, Who, blind ?? Blind(), events ?? [], CancellationToken.None);

    private static async Task<bool> HasIdentityAsync(RecoveryTestFactory target)
    {
        using var scope = target.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync() is not null;
    }

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
        (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE id = @id COLLATE NOCASE", new { id = src.HardDeletedArticle.ToString() }))
            .Should().Be(0);
    }

    // ---------------------------------------------------------------- the archive a blind node serves

    [Fact]
    public async Task A_signed_format2_archive_restores_through_the_reader_and_keeps_the_blind_node_as_a_peer_at_cp_sequence()
    {
        using var target = new RecoveryTestFactory();

        var result = await RestoreArchiveAsync(target, src.ArchivePath, blind: Blind(cpSeq: 0));

        await AssertRestoredLikeTheSourceAsync(target, src, result);
        using var scope = target.Services.CreateScope();
        var blindRow = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(src.ServingNodeId);
        blindRow.Should().NotBeNull("the node the package came from is this device's peer");
        blindRow!.ApiAddress.Should().Be(BlindAddress);
        var position = await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(src.ServingNodeId);
        position!.LastSequenceNum.Should().Be(src.Deltas.Max(d => d.SequenceNum), "everything the package covered is pulled");
    }

    [Fact]
    public async Task The_package_format_is_told_by_the_blind_manifest()
    {
        (await RecoveryRestoreService.IsFormat2PackageAsync(src.ArchivePath)).Should().BeTrue();
        var notAPackage = Path.Combine(_dir, "garbage.tar.gz");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(notAPackage, [1, 2, 3]);
        (await RecoveryRestoreService.IsFormat2PackageAsync(notAPackage)).Should().BeFalse("the format-1 path then says what is wrong with it");
    }

    [Fact]
    public async Task An_entry_changed_after_the_serving_node_signed_is_refused_and_nothing_is_written()
    {
        var tampered = await ReplaceEntryAsync(src.ArchivePath, "deltas.jsonl", bytes => [.. bytes, (byte)' ']);
        using var target = new RecoveryTestFactory();

        var act = () => RestoreArchiveAsync(target, tampered, signature: null);

        // The detached signature covers the whole archive, so the changed archive fails there first.
        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("signature");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task An_entry_changed_with_a_detached_signature_made_for_it_is_still_refused_by_the_signed_manifest()
    {
        var tampered = await ReplaceEntryAsync(src.ArchivePath, "deltas.jsonl", bytes => [.. bytes, (byte)' ']);
        // A transport with no detached signature (a seed upload): the embedded signature and the hashes it signs carry the whole package.
        using var target = new RecoveryTestFactory();

        var act = () => RestoreArchiveAsync(target, tampered, signature: []);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("hash");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_package_signed_by_another_key_than_the_one_the_restore_code_names_is_refused()
    {
        var (otherKey, _) = Ed25519Signer.GenerateKeyPair();
        using var target = new RecoveryTestFactory();

        var act = () => RestoreArchiveAsync(target, src.ArchivePath, servingKey: otherKey);

        await act.Should().ThrowAsync<InvalidDataException>();
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_package_that_names_another_serving_node_than_the_one_it_came_from_is_refused()
    {
        using var target = new RecoveryTestFactory();

        var act = () => RestoreArchiveAsync(target, src.ArchivePath, blind: Blind(node: Guid.NewGuid()));

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("node it came from");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task Deltas_over_the_limit_are_left_for_the_first_sync_and_the_restore_says_so()
    {
        var before = BaselineRestoreRun.MaxDeltas;
        BaselineRestoreRun.MaxDeltas = 4;
        try
        {
            using var target = new RecoveryTestFactory();

            var result = await RestoreArchiveAsync(target, src.ArchivePath);

            result.Replay!.Incomplete.Should().BeTrue();
            result.Replay.Total.Should().Be(4);
            result.Anchor.Confirmed.Should().BeFalse("events are missing");
            using var scope = target.Services.CreateScope();
            var position = await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(src.ServingNodeId);
            position!.LastSequenceNum.Should().Be(result.Replay.LastSequence, "the first sync takes the rest from where the replay stopped");
            result.Replay.LastSequence.Should().BeLessThan(src.Deltas.Max(d => d.SequenceNum));
        }
        finally
        {
            BaselineRestoreRun.MaxDeltas = before;
        }
    }

    // ---------------------------------------------------------------- a Docker blind node's backup folder

    [Fact]
    public async Task A_docker_blind_backup_folder_restores_from_its_baseline_folder_and_its_log()
    {
        var folder = await src.BuildBlindBackupFolderAsync("folder-ok");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        await AssertRestoredLikeTheSourceAsync(target, src, result);
    }

    [Fact]
    public async Task A_backup_whose_baseline_folder_is_missing_is_refused_and_nothing_is_written()
    {
        var folder = await src.BuildBlindBackupFolderAsync("folder-nobaseline", withBaselineFolder: false);
        using var target = new RecoveryTestFactory();

        var act = () => target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("baseline");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_backup_whose_baseline_signature_is_not_a_superadmins_is_refused()
    {
        var folder = await src.BuildBlindBackupFolderAsync("folder-badsig");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(folder, "beememorybank.db")};Pooling=False"))
        {
            db.Open();
            var signature = await db.ExecuteScalarAsync<byte[]>("SELECT signature FROM tbl_baseline WHERE is_current = 1");
            signature![0] ^= 0xFF;
            await db.ExecuteAsync("UPDATE tbl_baseline SET signature = @s WHERE is_current = 1", new { s = signature });
        }
        SqliteConnection.ClearAllPools();
        using var target = new RecoveryTestFactory();

        var act = () => target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(folder, Who);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("superadmin");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task A_backup_of_a_node_that_never_adopted_a_baseline_is_not_taken_for_one()
    {
        var folder = await src.BuildBlindBackupFolderAsync("folder-nobaselinerow");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(folder, "beememorybank.db")};Pooling=False"))
        {
            db.Open();
            await db.ExecuteAsync("DELETE FROM tbl_baseline");
        }
        SqliteConnection.ClearAllPools();

        var source = await BackupFolderRestoreSource.OpenAsync(Path.Combine(folder, "beememorybank.db"), Path.Combine(folder, "baselines", "current"));

        source.Should().BeNull("a database copy without a current baseline is restored the way it always was");
    }

    // ---------------------------------------------------------------- an Android blind backup file

    [Fact]
    public async Task An_android_backup_holding_a_format2_package_restores_with_the_pairing_checked_against_the_phones_anchors()
    {
        var file = await AndroidBackupAsync();
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single()
            .RestoreAsync(file, Who, CancellationToken.None, RestoreBoxPolicy.Default);

        await AssertRestoredLikeTheSourceAsync(target, src, result);
        using var scope = target.Services.CreateScope();
        var listener = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(src.ServingNodeId);
        listener!.ApiAddress.Should().Be(BlindAddress, "the restored device dials the node the phone called");
    }

    [Fact]
    public async Task An_android_backup_whose_pairing_no_anchor_vouches_for_is_refused_and_nothing_is_written()
    {
        var file = await AndroidBackupAsync(events: []);
        using var target = new RecoveryTestFactory();

        var act = () => target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single()
            .RestoreAsync(file, Who, CancellationToken.None, RestoreBoxPolicy.Default);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("anchor");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    [Fact]
    public async Task An_android_backup_paired_by_a_node_that_is_no_superadmin_in_the_package_is_refused()
    {
        var file = await AndroidBackupAsync(pairedBy: src.ServingNodeId);
        using var target = new RecoveryTestFactory();

        var act = () => target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single()
            .RestoreAsync(file, Who, CancellationToken.None, RestoreBoxPolicy.Default);

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("superadmin");
        (await HasIdentityAsync(target)).Should().BeFalse();
    }

    // ---------------------------------------------------------------- builders

    /// <summary>The phone's backup file: the archive, its signature and the phone's events as the body; the pairing record sealed under the current key.</summary>
    internal async Task<string> AndroidBackupAsync(IReadOnlyList<SyncEvent>? events = null, Guid? pairedBy = null)
    {
        Directory.CreateDirectory(_dir);
        var phoneId = BlindNodeId.NewId();
        var key = RandomNumberGenerator.GetBytes(32);
        RecoverySet set;
        using (var control = new SqliteConnection($"Data Source={src.Package.ControlDbPath};Mode=ReadOnly;Pooling=False"))
        {
            control.Open();
            set = await RecoverySetBuilder.BuildAsync(control);
        }
        var dek = src.Source.Services.GetRequiredService<SessionService>().GetMasterDek();
        var name = SealedSecretService.AndroidBackupName(phoneId).ToLowerInvariant();

        var unsigned = new BlindPhoneBackupSeal(key, src.ServingNodeId, src.ServingPublicKey, BlindAddress, BlindPin, Guid.Empty, []);
        using (var scope = src.Source.Services.CreateScope())
        {
            var me = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var signedBy = pairedBy ?? me.NodeId;
            var named = unsigned with { PairedBy = signedBy };
            var seal = named with
            {
                PairingSignature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(me, named.PairingStatement(phoneId)),
            };
            var (wrapped, iv) = SealedSecretCrypto.Seal(name, seal.Encode(), dek);
            set = set with
            {
                SealedSecrets = [.. set.SealedSecrets, new RecoverySetSecret(name, DekFingerprint.Of(dek),
                    Convert.ToBase64String(wrapped), Convert.ToBase64String(iv), DateTime.UtcNow.ToString("O"))],
            };
        }

        // What the phone holds: the signed events it pulled; the sealed ones stay in the package, so these are the clear ones (the anchors).
        var held = events ?? src.Deltas.Where(d => d.EventType != EventClasses.Sealed).ToList();
        var eventsPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".events.json");
        await File.WriteAllTextAsync(eventsPath, JsonSerializer.Serialize(held, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var body = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".body");
        await BlindPhoneBackupBody.WriteAsync(body, src.ArchivePath, src.ArchiveSignature, eventsPath);
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + AndroidBackupRestore.Extension);
        await AndroidBackupWriter.WriteAsync(body, file, key, phoneId, SealedSecretService.AndroidBackupName(phoneId), set.ToJson());
        return file;
    }

    /// <summary>A copy of <paramref name="archive"/> with one entry's bytes changed, nothing else (so the signatures no longer fit it).</summary>
    private async Task<string> ReplaceEntryAsync(string archive, string entry, Func<byte[], byte[]> change)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".tar.gz");
        await using var input = File.OpenRead(archive);
        await using var gzipIn = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzipIn);
        await using var output = File.Create(path);
        await using var gzipOut = new GZipStream(output, CompressionLevel.Fastest);
        await using var writer = new TarWriter(gzipOut, TarEntryFormat.Pax);
        while (await reader.GetNextEntryAsync() is { } e)
        {
            using var bytes = new MemoryStream();
            if (e.DataStream is not null) await e.DataStream.CopyToAsync(bytes);
            var data = e.Name == entry ? change(bytes.ToArray()) : bytes.ToArray();
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, e.Name) { DataStream = new MemoryStream(data) });
        }
        return path;
    }
}
