using System.Buffers.Text;
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
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Weak test: an Android blind backup restored after the PC that paired the phone gave itself a new identity key, through
/// <see cref="AndroidBackupRestoreSource"/> - the pairing record the re-key signed again, the package the listening node built after it took the
/// rotation, and the phone's events (the anchors of both keys). Its own fixture instance: the PC is re-keyed, which the shared one must not be.
/// </summary>
public class AndroidBackupRestoreAfterRekeyTests : IAsyncLifetime
{
    private const string ListenerAddress = "https://blind.test:5610";
    private static readonly string ListenerPin = Base64Url.EncodeToString(SHA256.HashData("listener"u8));
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", RestoreSourceFixture.Password);
    private readonly RestoreSourceFixture _src = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_android_rekey_" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _src.InitializeAsync();

    public async Task DisposeAsync() => await _src.DisposeAsync();

    [Fact]
    public async Task A_backup_made_after_the_pairing_pc_rekeyed_restores_with_the_record_the_rekey_signed_again()
    {
        var phoneId = BlindNodeId.NewId();
        var backupKey = RandomNumberGenerator.GetBytes(32);
        var name = SealedSecretService.AndroidBackupName(phoneId);

        // The PC pairs the phone (its key K1 signs the record), then gives itself a new identity key K2.
        NodeIdentity k1;
        using (var scope = _src.Source.Services.CreateScope())
        {
            k1 = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            var unsigned = new BlindPhoneBackupSeal(backupKey, _src.BlindNodeIdentity, _src.ProducerKey, ListenerAddress, ListenerPin, k1.NodeId, []);
            var seal = unsigned with
            {
                PairingSignature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(k1, unsigned.PairingStatement(phoneId)),
            };
            await scope.ServiceProvider.GetRequiredService<SealedSecretService>().PublishAsync(name, seal.Encode());
        }
        using (var conn = _src.Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_whitelist SET last_capabilities = @caps", new { caps = string.Join(",", MetadataCapabilities.Advertised) });
        byte[] k2;
        using (var scope = _src.Source.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "test");
            result.Done.Should().BeTrue(result.Refusal);
            k2 = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.Ed25519PublicKey;
        }
        var record = await OpenRecordAsync(name);
        record.PairedBy.Should().Be(k1.NodeId);
        Ed25519Signer.Verify(k2, record.PairingStatement(phoneId), record.PairingSignature).Should().BeTrue("the re-key signed the record again");

        // The listening node takes the rotation and the anchor the new key signed, as it would by sync; then builds the package the phone holds.
        using (var pcScope = _src.Source.Services.CreateScope())
        using (var blindScope = _src.Blind.Services.CreateScope())
        {
            var log = pcScope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var applier = blindScope.ServiceProvider.GetRequiredService<EventApplier>();
            foreach (var evt in await log.GetRecentAsync(10, 0, EventTypes.NodeKeyRotated))
                (await applier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
            foreach (var evt in (await log.GetRecentAsync(100, 0, EventTypes.StateAnchor)).OrderBy(e => e.LamportTs))
                await applier.ApplyAsync(evt);
            (await blindScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(k1.NodeId))!
                .Ed25519PublicKey.Should().Equal(k2, "the listening node follows the PC's new key");
        }
        Directory.CreateDirectory(_dir);
        string package, signature;
        List<SyncEvent> events;
        using (var scope = _src.Blind.Services.CreateScope())
        {
            var built = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: null, producerIsSuperadmin: false);
            package = Path.Combine(_dir, "package.tar.gz");
            signature = package + ".sig";
            File.Copy(built.FilePath, package);
            File.Copy(built.FilePath + ".sig", signature);
            events = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(0, 100_000)).ToList();
        }
        events.Should().Contain(e => e.EventType == EventTypes.StateAnchor && e.NodeId == k1.NodeId);

        var file = await AndroidBackupAsync(phoneId, backupKey, record, package, signature, events);
        using var target = new RecoveryTestFactory();
        var restored = await target.Services.GetServices<IBackupFileRestoreSource>().OfType<AndroidBackupRestoreSource>().Single()
            .RestoreAsync(file, Who, CancellationToken.None);

        restored.Anchor.Found.Should().BeTrue();
        using var check = target.Services.CreateScope();
        var whitelist = check.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await whitelist.GetByNodeIdAsync(_src.BlindNodeIdentity))!.ApiAddress.Should().Be(ListenerAddress, "the restored device dials the node the phone called");
        (await whitelist.GetByNodeIdAsync(k1.NodeId, includeDeleted: true))!.Ed25519PublicKey.Should().Equal(k2, "the PC comes back with its new key");
        var session = target.Services.GetRequiredService<SessionService>();
        (await session.UnlockAsync(RestoreSourceFixture.Password)).Should().BeTrue();
        foreach (var (id, body) in _src.Bodies)
            (await check.ServiceProvider.GetRequiredService<ArticleService>().GetContentAsync(id)).Should().Be(body);
    }

    private async Task<BlindPhoneBackupSeal> OpenRecordAsync(string name)
    {
        using var c = _src.Source.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var row = await c.QuerySingleAsync<(byte[] Wrapped, byte[] Iv)>("SELECT wrapped, iv FROM tbl_sealed_secret WHERE name = @name COLLATE NOCASE", new { name });
        var dek = _src.Source.Services.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            BlindPhoneBackupSeal.TryDecode(SealedSecretCrypto.TryOpen(name.ToLowerInvariant(), row.Wrapped, row.Iv, dek), out var seal).Should().BeTrue();
            return seal!;
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    /// <summary>The phone's backup file, as AndroidBackupRestoreScenarioTests builds it: the record sealed in the recovery set under the current key.</summary>
    private async Task<string> AndroidBackupAsync(Guid phoneId, byte[] key, BlindPhoneBackupSeal record, string package, string signaturePath,
        IReadOnlyList<SyncEvent> events)
    {
        var set = RecoverySet.Parse(await _src.Blind.Services.GetRequiredService<RecoverySetBuilder>().BuildJsonAsync());
        var dek = _src.Source.Services.GetRequiredService<SessionService>().GetMasterDek();
        var name = SealedSecretService.AndroidBackupName(phoneId).ToLowerInvariant();
        var (wrapped, iv) = SealedSecretCrypto.Seal(name, record.Encode(), dek);
        set = set with
        {
            SealedSecrets = [.. set.SealedSecrets, new RecoverySetSecret(name, DekFingerprint.Of(dek),
                Convert.ToBase64String(wrapped), Convert.ToBase64String(iv), DateTime.UtcNow.ToString("O"))]
        };
        var eventsPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".events.json");
        await File.WriteAllTextAsync(eventsPath, JsonSerializer.Serialize(events, JsonOpts));
        var body = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".body");
        await BlindPhoneBackupBody.WriteAsync(body, package, await File.ReadAllBytesAsync(signaturePath), eventsPath);
        var file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + AndroidBackupRestore.Extension);
        await AndroidBackupWriter.WriteAsync(body, file, key, phoneId, SealedSecretService.AndroidBackupName(phoneId), set.ToJson());
        return file;
    }
}
