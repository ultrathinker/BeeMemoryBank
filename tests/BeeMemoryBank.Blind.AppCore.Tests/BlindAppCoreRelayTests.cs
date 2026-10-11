using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Step A relay on an AppCore blind copy (docs/design/step-a-spec.md 9.4, red tests 16.3 items 1-2), through the composition
/// a phone or desktop copy runs: before this copy applied the activation, clear content is materialised as 2.5.1 did (the
/// control: the scanner finds its canary in the database); after it, sealed events are stored exactly as the author signed
/// them and never applied, and clear content is accepted without being stored, so no canary of it reaches the file or WAL.
/// </summary>
public sealed class BlindAppCoreRelayTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-relay-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;
    private readonly Guid _author = Guid.NewGuid();
    private byte[] _authorKey = [];
    private long _lamport = 10;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(_dir, Path.Combine(_dir, "beememorybank.db")));
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        _authorKey = key;
        await _sp.GetRequiredService<INodeIdentityRepository>().CreateAsync(new NodeIdentity
        {
            NodeId = BlindNodeId.NewId(), DisplayName = "phone", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow,
        });
        await _sp.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = _author, DisplayName = "PC", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = _author,
        });
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task AnActivatedBlindCopy_StoresSealedEventsAsSent_AndDropsClearContent()
    {
        // Control: before the activation a clear folder_create is materialised, its name in the database.
        var before = MetadataCanaries.Ascii("beforeactivation");
        (await ApplyAsync(Clear(before))).Should().Be(EventApplyResult.Applied);
        await CheckpointAsync();
        MetadataCanaries.ScanDirectory(_dir, [before]).Should().NotBeEmpty("the scanner finds what a 2.5 copy materialised");

        await ActivateAsync();
        var after = MetadataCanaries.NonAscii("afteractivation");
        var sealedEvents = Enumerable.Range(0, 3).Select(_ => Sealed()).ToList();
        foreach (var evt in sealedEvents)
            (await ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
        var straggler = Clear(after);
        (await ApplyAsync(straggler)).Should().Be(EventApplyResult.SilentlyDropped);

        using var conn = _sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        foreach (var evt in sealedEvents)
        {
            var row = await conn.QuerySingleAsync<(string Payload, byte[] Signature, long ApplyState, string EventType)>(
                "SELECT payload, signature, apply_state, event_type FROM tbl_event WHERE event_id = @id COLLATE NOCASE", new { id = evt.EventId.ToString() });
            row.Payload.Should().Be(evt.Payload, "stored as the author sent it");
            row.Signature.Should().Equal(evt.Signature);
            row.EventType.Should().Be(EventClasses.Sealed);
            row.ApplyState.Should().Be(EventApplyStates.Relayed);
        }
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_id = @id COLLATE NOCASE", new { id = straggler.EventId.ToString() }))
            .Should().Be(0);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_folder")).Should().Be(1, "only the folder from before the activation");
        await CheckpointAsync();
        MetadataCanaries.ScanDirectory(_dir, [after]).Should().BeEmpty("the dropped clear content reached neither the file nor its WAL");
    }

    private async Task<EventApplyResult> ApplyAsync(SyncEvent evt)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt, new ApplyContext(ReceivedFrom: _author, ReceivedSeq: _lamport));
    }

    /// <summary>Spec 14: a key row and an activation row, and the policy told.</summary>
    private async Task ActivateAsync()
    {
        using (var conn = _sp.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("O");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
                  VALUES (@k, 1, @fp, 0, @w, @iv, @a, @now, 1, @e)",
                new { k = new string('c', 32), fp = new string('f', 64), w = new byte[48], iv = new byte[12], a = _author.ToString(), now, e = Guid.NewGuid().ToString() });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
                  VALUES (@id, @k, 1, @now, @a, @e, 2, @now)",
                new { id = Guid.NewGuid().ToString(), k = new string('c', 32), now, a = _author.ToString(), e = Guid.NewGuid().ToString() });
        }
        _sp.GetRequiredService<ISyncProtocolPolicy>().MarkActivated();
    }

    private async Task CheckpointAsync()
    {
        using var conn = _sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("PRAGMA wal_checkpoint(PASSIVE)");
    }

    private SyncEvent Clear(string name) => Sign(new SyncEvent
    {
        EventType = EventTypes.FolderCreate, ProtocolVersion = 3,
        Payload = JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), "/" + name, name, "/", DateTime.UtcNow, DateTime.UtcNow)),
    });

    /// <summary>A sealed event as an activated author sends it; a blind copy cannot open it, so its ciphertext is opaque bytes here.</summary>
    private SyncEvent Sealed() => Sign(new SyncEvent
    {
        EventType = EventClasses.Sealed, ProtocolVersion = EventClasses.SealedProtocol, ArticleId = null,
        Payload = new SealedEnvelope(new string('c', 32), RandomNumberGenerator.GetBytes(12), [], RandomNumberGenerator.GetBytes(16 + 128)).ToPayload(),
    });

    private SyncEvent Sign(SyncEvent evt)
    {
        evt.EventId = Guid.NewGuid();
        evt.NodeId = _author;
        evt.LamportTs = ++_lamport;
        evt.CreatedAt = DateTime.UtcNow;
        evt.Signature = Ed25519Signer.Sign(_authorKey, EventSignature.BuildPayload(evt));
        return evt;
    }
}
