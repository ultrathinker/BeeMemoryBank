extern alias WebApp;

using System.Buffers.Text;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindPhone;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using AndroidBackupFileRecognizer = WebApp::BeeMemoryBank.Web.Services.AndroidBackupFileRecognizer;
using SetupModel = WebApp::BeeMemoryBank.Web.Pages.SetupModel;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Restore on a new Windows device from nothing but an Android blind node's backup file and the master
/// password (plan 6.8, section 10). The body is the blind package the phone's listening node signed (its
/// replica), its signature and the phone's signed events; the producer is the one the DEK-sealed pairing
/// record names. So the restore is the package restore a blind node's network restore gets — signatures,
/// signed blind manifest, positions, media, anchor-vouched peers — and a body the phone forged (it holds the
/// backup key) is refused. Uses <see cref="RestoreSourceFixture"/> (two rotations, a late body under key 1).
/// </summary>
[Collection(RestoreScenarioCollection.Name)]
public class AndroidBackupRestoreScenarioTests(RestoreSourceFixture source) : IDisposable
{
    private const string ListenerAddress = "https://blind.test:5610";
    private static readonly string ListenerPin = Base64Url.EncodeToString(SHA256.HashData("listener"u8));
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", RestoreSourceFixture.Password);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_android_restore_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FromTheFileAndTheMasterPassword_EverythingOpens_AndTheListenerIsTheFirstPeer()
    {
        var backup = await AndroidBackupAsync();
        using var target = new RecoveryTestFactory();

        var result = await Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        result.RetiredKeys.Should().Be(2, "both older keys come back through the chain");
        result.Anchor.Found.Should().BeTrue("the phone's signed events carry the anchor");
        var session = target.Services.GetRequiredService<SessionService>();
        (await session.UnlockAsync(RestoreSourceFixture.Password)).Should().BeTrue();
        DekFingerprint.Of(session.GetMasterDek()).Should().Be(source.CurrentFingerprint);
        using var scope = target.Services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        foreach (var (id, body) in source.Bodies)
            (await articles.GetContentAsync(id)).Should().Be(body);

        var listener = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(source.BlindNodeIdentity))!;
        listener.ApiAddress.Should().Be(ListenerAddress, "the restored device dials the node the phone called");
        listener.TlsSpki.Should().Be(ListenerPin, "pinned as the pairing recorded it");
        var position = await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(source.BlindNodeIdentity);
        position!.LastSequenceNum.Should().BeGreaterThan(0,
            "the first sync goes on from the package's cut, not from the listener's first event");
    }

    [Fact]
    public async Task TheMediaOfThePackage_AreRestored()
    {
        // A legacy media row (no ciphertext hash, no copy in any table): its bytes exist only as the package's
        // media/<id>.enc. Added to the blind node just for this package, then taken out again — the network is
        // shared by the collection and its anchor must keep matching.
        var id = Guid.NewGuid();
        var media = Path.Combine(source.Blind.DataPath, "media");
        Directory.CreateDirectory(media);
        var name = $"{id}.enc";
        var bytes = RandomNumberGenerator.GetBytes(3000);
        await File.WriteAllBytesAsync(Path.Combine(media, name), bytes);
        var connFactory = source.Blind.Services.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>();
        string package, signature;
        try
        {
            using (var conn = connFactory.CreateConnection())
                await Dapper.SqlMapper.ExecuteAsync(conn,
                    // Newer than the anchor (the PC's, at a far later Lamport time): the anchor's cut is unchanged.
                    @"INSERT INTO tbl_media (id, file_name, content_type, file_size, encrypted_dek, dek_iv, iv, created_at, lamport_ts, source_node_id)
                      VALUES (@Id, 'legacy.bin', 'application/octet-stream', 3000, @Blob, @Blob, @Blob, @Now, @Lamport, @Source)",
                    new
                    {
                        Id = id.ToString().ToUpperInvariant(), Blob = new byte[12], Now = DateTime.UtcNow.ToString("O"),
                        Lamport = 1L << 40, Source = source.SourceNodeId.ToString()
                    });
            (package, signature) = await BlindPackageAsync(source.Blind);
        }
        finally
        {
            using (var conn = connFactory.CreateConnection())
                await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM tbl_media WHERE id = @Id", new { Id = id.ToString().ToUpperInvariant() });
            File.Delete(Path.Combine(media, name)); // this test's own file, not part of the shared network
        }
        var backup = await AndroidBackupAsync(package: package, signaturePath: signature);
        using var target = new RecoveryTestFactory();

        await Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(target.DataPath, "media", name)).Should().Equal(bytes);
    }

    [Fact]
    public async Task AWrongMasterPassword_IsRefused_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync();
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who with { Password = "WrongPassword9" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task AWrongSealedKey_IsRefused_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync(sealedKey: RandomNumberGenerator.GetBytes(32));
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task ABitFlippedBody_IsRefused_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync();
        var bytes = File.ReadAllBytes(backup);
        bytes[^100] ^= 0x04;
        File.WriteAllBytes(backup, bytes);
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        await AssertUninitializedAsync(target);
    }

    [Theory]
    [InlineData("other phone")] // sealed for another phone
    [InlineData("bare key")]    // a key with no producer (an older pairing record)
    public async Task WithoutThisPhonesPairingRecord_IsRefused_AndTheNodeStaysEmpty(string record)
    {
        var backup = record == "other phone"
            ? await AndroidBackupAsync(sealFor: BlindNodeId.NewId())
            : await AndroidBackupAsync(bareKey: true);
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*no sealed key for this phone*");
        await AssertUninitializedAsync(target);
    }

    /// <summary>
    /// The phone holds the backup key, so it can write any body: here its database says an intruder is a
    /// superadmin and the manifest's hashes are regenerated to match. The listener's signature is what it
    /// cannot redo.
    /// </summary>
    [Fact]
    public async Task ATamperedDatabase_WithItsManifestRegenerated_IsRefused_AndTheNodeStaysEmpty()
    {
        var tampered = await RepackAsync(source.PackagePath, db =>
        {
            using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
                                VALUES ($id, 'Intruder', $key, 'A', 1, $now, $now)";
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$key", Ed25519Signer.GenerateKeyPair().publicKey);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
        var backup = await AndroidBackupAsync(package: tampered);
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*signature*");
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task APackageFromAnotherProducer_IsRefused_AndTheNodeStaysEmpty()
    {
        // Genuine and signed — by the PC, not by the node the pairing record names.
        var (package, signature) = await BlindPackageAsync(source.Source);
        var backup = await AndroidBackupAsync(package: package, signaturePath: signature);
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task AGenericSnapshot_WithoutTheSignedBlindManifest_IsRefused_AndTheNodeStaysEmpty()
    {
        // Signed by the right node, but an ordinary snapshot: no blind-manifest.json to take whitelist,
        // positions and producer from.
        string package;
        using (var scope = source.Blind.Services.CreateScope())
        {
            var snapshots = scope.ServiceProvider.GetRequiredService<SnapshotService>();
            var info = await snapshots.CreateAsync(filterSecrets: true, sign: true, encryptDb: false);
            package = Path.Combine(_dir, "generic.tar.gz");
            Directory.CreateDirectory(_dir);
            File.Copy(snapshots.GetSnapshotPath(info.FileName), package);
            File.Copy(snapshots.GetSnapshotPath(info.FileName) + ".sig", package + ".sig");
        }
        var backup = await AndroidBackupAsync(package: package, signaturePath: package + ".sig");
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*blind-manifest*");
        await AssertUninitializedAsync(target);
    }

    /// <summary>
    /// The sealed secret is an any-peer event: another node can publish a newer <c>android-backup:&lt;phone&gt;</c>
    /// under the DEK. A replacement signed by a node that is not a superadmin — here the blind node itself, a
    /// genuine member of the network — names no producer the restore will accept.
    /// </summary>
    [Fact]
    public async Task AReplacementSeal_SignedByANodeThatIsNoSuperadmin_IsRefused_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync(pairing: PairedBy(source.Blind));
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*not signed by a superadmin*");
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task AReplacementSeal_NamingTheSuperadmin_WithoutItsSignature_IsRefused_AndTheNodeStaysEmpty()
    {
        var (_, forger) = Ed25519Signer.GenerateKeyPair();
        var backup = await AndroidBackupAsync(pairing: (seal, phone) => Task.FromResult(seal with
        {
            PairedBy = source.SourceNodeId,
            PairingSignature = Ed25519Signer.Sign(forger, seal.PairingStatement(phone)),
        }));
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*not signed by a superadmin*");
        await AssertUninitializedAsync(target);
    }

    /// <summary>
    /// A peer re-publishes the genuine pairing record with the PC's signature kept and only the backup key
    /// swapped for one of its own — here the key this body is actually under. The signature binds the key's
    /// digest, so the record no longer counts.
    /// </summary>
    [Fact]
    public async Task AReplacementSeal_WithOnlyTheBackupKeySwapped_AndTheSignatureKept_IsRefused_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync(pairing: async (seal, phone) =>
        {
            var genuine = await PairedBy(source.Source)(seal with { BackupKey = RandomNumberGenerator.GetBytes(32) }, phone);
            return genuine with { BackupKey = seal.BackupKey };
        });
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*not signed by a superadmin*");
        await AssertUninitializedAsync(target);
    }

    /// <summary>
    /// A full peer that turned hostile builds a package of its own — itself the producer, itself a superadmin in
    /// that package's manifest — signs a replacement pairing record with its own key and publishes it. Everything
    /// it made verifies against itself; what it cannot make is an anchor from the phone's events vouching for it.
    /// </summary>
    [Fact]
    public async Task AHostilePeersOwnPackage_UnderASealItSignedItself_IsRefused_ForWantOfAnAnchor()
    {
        using var hostile = new RecoveryTestFactory();
        await hostile.InitializeNodeAsync(password: "HostilePass1");
        await hostile.Services.GetRequiredService<SessionService>().UnlockAsync("HostilePass1");
        var (package, signature) = await BlindPackageAsync(hostile, producerIsSuperadmin: true);
        NodeIdentity me;
        using (var scope = hostile.Services.CreateScope())
            me = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var backup = await AndroidBackupAsync(package: package, signaturePath: signature,
            producer: (me.NodeId, me.Ed25519PublicKey), pairing: PairedBy(hostile));
        using var target = new RecoveryTestFactory();

        var act = () => Source(target).RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*No anchor under the master key vouches*");
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task MoreEventsThanARestoreTakes_AreRefused_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync(eventCopies: 4);
        using var target = new RecoveryTestFactory();
        var limited = Limited(target, BlindPhoneBackupLimits.Default with { MaxEvents = 3 * source.BlindEvents.Count });

        var act = () => limited.RestoreAsync(backup, Who, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*more events than a restore takes*");
        await AssertUninitializedAsync(target);
    }

    [Fact]
    public async Task ABodyLargerThanARestoreTakes_IsRefusedWhileDecrypting_AndTheNodeStaysEmpty()
    {
        var backup = await AndroidBackupAsync();
        using var target = new RecoveryTestFactory();
        var limits = new BlindPhoneBackupLimits(MaxPackageBytes: 1024, MaxEventsBytes: 1024, MaxEvents: 10);
        new FileInfo(backup).Length.Should().BeGreaterThan(limits.MaxBodyBytes + 4096, "the body must be over the bound to test it");
        var limited = Limited(target, limits);

        var act = () => limited.RestoreAsync(backup, Who, CancellationToken.None);

        // Refused by the decryption itself, before the body is staged — not only by the entry sizes after it.
        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("The backup's body is larger than a restore takes.");
        await AssertUninitializedAsync(target);
    }

    /// <summary>
    /// The way "Open an existing profile" and the restore form reach it: the Setup page recognises the file
    /// as a backup (not a profile to copy), and the path handed to BlindRestoreClient goes to this source.
    /// </summary>
    [Fact]
    public async Task OpenAnExistingProfile_RecognisesTheFile_AndRestoresFromIt()
    {
        var backup = await AndroidBackupAsync();
        SetupModel.LooksLikeBlindBackup(backup, [new AndroidBackupFileRecognizer()]).Should().BeTrue();
        SetupModel.LooksLikeBlindBackup(backup).Should().BeFalse("without the recognizer a file is not a backup folder");
        using var target = new RecoveryTestFactory();

        var result = await target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(backup, Who);

        result.RetiredKeys.Should().Be(2);
        (await target.Services.GetRequiredService<SessionService>().UnlockAsync(RestoreSourceFixture.Password)).Should().BeTrue();
    }

    [Fact]
    public async Task Handles_OnlyAndroidBackupFiles()
    {
        using var target = new RecoveryTestFactory();
        var android = Source(target);

        android.Handles(await AndroidBackupAsync()).Should().BeTrue();
        android.Handles(Path.Combine(source.BackupFolder, BlindPackageFile.DbFileName)).Should().BeFalse();
        android.Handles(source.PackagePath).Should().BeFalse();
        android.Handles(source.BackupFolder).Should().BeFalse();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static AndroidBackupRestoreSource Limited(RecoveryTestFactory target, BlindPhoneBackupLimits limits) =>
        new(target.Services.GetRequiredService<RecoveryRestoreService>(), target.Services.GetRequiredService<IServiceScopeFactory>(), limits);

    /// <summary>The source as the Api host registers it.</summary>
    private static AndroidBackupRestoreSource Source(RecoveryTestFactory target) =>
        target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single();

    /// <summary>
    /// The phone's backup: by default the blind node's signed package with its signature and events as the
    /// body, and the pairing record — backup key + the blind node as the producer — sealed under the current
    /// DEK as <c>android-backup:&lt;phone id&gt;</c> in the header's recovery set.
    /// </summary>
    private async Task<string> AndroidBackupAsync(byte[]? sealedKey = null, Guid? sealFor = null, bool bareKey = false,
        string? package = null, string? signaturePath = null, Func<BlindPhoneBackupSeal, Guid, Task<BlindPhoneBackupSeal>>? pairing = null,
        (Guid Id, byte[] Key)? producer = null, int eventCopies = 1)
    {
        Directory.CreateDirectory(_dir);
        var phoneId = BlindNodeId.NewId();
        var key = RandomNumberGenerator.GetBytes(32);

        var set = RecoverySet.Parse(await File.ReadAllTextAsync(Path.Combine(source.BackupFolder, "backup.recovery-set.json")));
        var dek = source.Source.Services.GetRequiredService<SessionService>().GetMasterDek();
        var name = SealedSecretService.AndroidBackupName(sealFor ?? phoneId).ToLowerInvariant();
        var unsigned = new BlindPhoneBackupSeal(sealedKey ?? key, producer?.Id ?? source.BlindNodeIdentity,
            producer?.Key ?? source.ProducerKey, ListenerAddress, ListenerPin, Guid.Empty, []);
        var value = bareKey ? (sealedKey ?? key) : (await (pairing ?? PairedBy(source.Source))(unsigned, phoneId)).Encode();
        var (wrapped, iv) = SealedSecretCrypto.Seal(name, value, dek);
        set = set with
        {
            SealedSecrets = [.. set.SealedSecrets, new RecoverySetSecret(name, DekFingerprint.Of(dek),
                Convert.ToBase64String(wrapped), Convert.ToBase64String(iv), DateTime.UtcNow.ToString("O"))]
        };

        var events = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".events.json");
        await File.WriteAllTextAsync(events, JsonSerializer.Serialize(Enumerable.Repeat(source.BlindEvents, eventCopies).SelectMany(e => e), JsonOpts));
        var signature = signaturePath != null ? await File.ReadAllBytesAsync(signaturePath) : source.Signature;
        var body = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".body");
        await BlindPhoneBackupBody.WriteAsync(body, package ?? source.PackagePath, signature, events);

        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + AndroidBackupRestore.Extension);
        await AndroidBackupWriter.WriteAsync(body, file, key, phoneId, SealedSecretService.AndroidBackupName(phoneId), set.ToJson());
        return file;
    }

    /// <summary>The pairing record as <paramref name="node"/> signs it when it pairs the phone (the PC, by default).</summary>
    private static Func<BlindPhoneBackupSeal, Guid, Task<BlindPhoneBackupSeal>> PairedBy(RecoveryTestFactory node) => async (seal, phone) =>
    {
        using var scope = node.Services.CreateScope();
        var me = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        return seal with
        {
            PairedBy = me.NodeId,
            PairingSignature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(me, seal.PairingStatement(phone)),
        };
    };

    /// <summary>A fresh blind package of <paramref name="node"/>, signed by it, copied here with its signature.</summary>
    private async Task<(string Package, string Signature)> BlindPackageAsync(RecoveryTestFactory node, bool producerIsSuperadmin = false)
    {
        Directory.CreateDirectory(_dir);
        using var scope = node.Services.CreateScope();
        var built = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
            .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: producerIsSuperadmin);
        var package = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".tar.gz");
        File.Copy(built.FilePath, package);
        File.Copy(built.FilePath + ".sig", package + ".sig");
        return (package, package + ".sig");
    }

    /// <summary>The package with its database changed and manifest.json's hashes made to match (the old signatures kept).</summary>
    private async Task<string> RepackAsync(string packagePath, Action<string> changeDatabase)
    {
        var dir = Path.Combine(_dir, "repack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using (var gz = new GZipStream(File.OpenRead(packagePath), CompressionMode.Decompress))
            await TarFile.ExtractToDirectoryAsync(gz, dir, overwriteFiles: true);
        var db = Path.Combine(dir, BlindPackageFile.DbFileName);
        changeDatabase(db);
        SqliteConnection.ClearAllPools();

        var manifestPath = Path.Combine(dir, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["files"]![BlindPackageFile.DbFileName] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(db)));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var repacked = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".tar.gz");
        await using (var fs = File.Create(repacked))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gz))
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                await tar.WriteEntryAsync(f, Path.GetRelativePath(dir, f).Replace('\\', '/'));
        return repacked;
    }

    private static async Task AssertUninitializedAsync(RecoveryTestFactory target)
    {
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync()).Should().BeFalse(
            "a refused restore must leave nothing behind");
    }
}
