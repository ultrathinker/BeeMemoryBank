using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 4.3, 5.4, 5.6): the protocol policy and the appliers of metadata_key_set and
/// metadata_activation. They run on every host, need no DEK, and only check shape; the superadmin gate decides who may
/// send them. Nodes use legacy v=0 identities so no test waits on Argon2.
/// </summary>
public class MetadataControlEventTests : IAsyncLifetime
{
    private readonly ConcreteFixture _node = new();
    private Guid _admin, _plain;
    private byte[] _adminSeed = [], _plainSeed = [];
    private long _lamport = 100;

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        await _node.NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(), DisplayName = "Receiver", Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
            Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow
        });
        (_admin, _adminSeed) = await TrustAsync("Admin", isSuperadmin: true);
        (_plain, _plainSeed) = await TrustAsync("Plain", isSuperadmin: false);
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    // ─── Policy ─────────────────────────────────────────────────────────────

    [Fact]
    public void BeforeActivation_TheNodeDeclares3_AndAcceptsEvery3Peer()
    {
        var policy = _node.ProtocolPolicy;
        policy.SealsMetadata.Should().BeFalse();
        policy.Declared.Should().Be(3);
        policy.IsCompatiblePeer(3, null).Should().BeTrue("a 2.5 peer still syncs before activation");
        policy.IsCompatiblePeer(4, null).Should().BeTrue();
        policy.IsCompatiblePeer(2, [MetadataCapabilities.EnvelopeV1]).Should().BeFalse();
        policy.IsCompatiblePeer(null, null).Should().BeFalse();
        policy.ShouldPullFrom(null).Should().BeTrue();
        policy.ShouldPullFrom(4).Should().BeTrue("this build applies protocol 4");
        policy.ShouldPullFrom(5).Should().BeFalse();
    }

    [Fact]
    public void AfterActivation_TheNodeDeclares4_AndAccepts3OnlyWithTheCapability()
    {
        var policy = _node.ProtocolPolicy;
        policy.MarkActivated();
        policy.SealsMetadata.Should().BeTrue();
        policy.Declared.Should().Be(4);
        policy.IsCompatiblePeer(4, null).Should().BeTrue();
        policy.IsCompatiblePeer(3, null).Should().BeFalse("a 2.5 peer is cut off after activation");
        policy.IsCompatiblePeer(3, ["something-else"]).Should().BeFalse();
        policy.IsCompatiblePeer(3, [MetadataCapabilities.EnvelopeV1]).Should().BeTrue("a 2.6 peer must still pull the activation");
        policy.IsCompatiblePeer(2, [MetadataCapabilities.EnvelopeV1]).Should().BeFalse();
    }

    [Fact]
    public async Task APolicy_ReadsTheActivationRecord_OfItsOwnDatabase()
    {
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
                  VALUES (@a, @k, 1, @t, @n, @e, 1, @t)",
                new { a = Guid.NewGuid().ToString(), k = NewKeyId(), t = DateTime.UtcNow.ToString("O"), n = _admin.ToString(), e = Guid.NewGuid().ToString() });

        var restarted = new SyncProtocolPolicy(_node.Factory);
        restarted.SealsMetadata.Should().BeTrue("a node restarted after its activation declares 4 from the start");
        restarted.Declared.Should().Be(4);
    }

    // ─── metadata_key_set ───────────────────────────────────────────────────

    [Fact]
    public async Task AKeyRecord_FromASuperadmin_IsStored_AndAsksForADrain()
    {
        var keyId = NewKeyId();
        var payload = KeyPayload(keyId, generation: 1);
        var evt = Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(payload));

        (await _node.EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);

        using var conn = _node.Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<(string KeyId, long Generation, string Fp, byte[] Wrapped, byte[] Iv, string Author, string EventId)>(
            "SELECT key_id, generation, dek_fingerprint, wrapped, iv, author_node_id, event_id FROM tbl_metadata_key");
        row.KeyId.Should().Be(keyId);
        row.Generation.Should().Be(1);
        row.Fp.Should().Be(payload.DekFingerprint);
        row.Wrapped.Should().Equal(Convert.FromBase64String(payload.Wrapped));
        row.Iv.Should().Equal(Convert.FromBase64String(payload.Iv));
        row.Author.Should().Be(_admin.ToString("D"));
        row.EventId.Should().Be(evt.EventId.ToString("D"));
        _node.Drain.Requests.Should().Be(1, "sealed events waiting for this key may open now");
        (await _node.EventLogRepo.ExistsAsync(evt.EventId)).Should().BeTrue("a control event is logged and relayed as any other");
    }

    [Fact]
    public async Task AKeyRecord_FromAPlainPeer_IsRefused()
    {
        var evt = Signed(_plain, _plainSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(NewKeyId(), 1)));
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(evt)).Should().ThrowAsync<OriginatorNotSuperadminException>();
        (await CountAsync("tbl_metadata_key")).Should().Be(0);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("generation")]
    [InlineData("fingerprint")]
    [InlineData("iv")]
    [InlineData("wrapped")]
    [InlineData("created")]
    public async Task AMalformedKeyRecord_IsRefused(string broken)
    {
        var p = KeyPayload(NewKeyId(), 1);
        p = broken switch
        {
            "key" => p with { KeyId = p.KeyId.ToUpperInvariant() },
            "generation" => p with { Generation = 0 },
            "fingerprint" => p with { DekFingerprint = "abc" },
            "iv" => p with { Iv = Convert.ToBase64String(new byte[11]) },
            "wrapped" => p with { Wrapped = Convert.ToBase64String(new byte[47]) },
            _ => p with { CreatedAt = "yesterday" },
        };
        var evt = Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(p));
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(evt)).Should().ThrowAsync<InvalidDataException>();
        (await CountAsync("tbl_metadata_key")).Should().Be(0);
        SyncFailureClassifier.Classify(new InvalidDataException()).Should().Be(SyncFailureKind.Permanent);
    }

    [Fact]
    public async Task AKeyRecord_IsWrittenOnce()
    {
        var keyId = NewKeyId();
        var first = KeyPayload(keyId, 1);
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(first)));
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(keyId, 2))));

        using var conn = _node.Factory.CreateConnection();
        var rows = (await conn.QueryAsync<(long Generation, byte[] Wrapped)>("SELECT generation, wrapped FROM tbl_metadata_key")).ToList();
        rows.Should().ContainSingle();
        rows[0].Generation.Should().Be(1);
        rows[0].Wrapped.Should().Equal(Convert.FromBase64String(first.Wrapped));
    }

    // ─── metadata_activation ────────────────────────────────────────────────

    [Fact]
    public async Task AnActivation_BeforeItsKey_IsDeferred_ThenSwitchesTheNode_AndTellsTheObserversOnce()
    {
        var observer = new RecordingObserver();
        _node.ActivationObservers.Add(observer);
        var keyId = NewKeyId();
        var activationId = Guid.NewGuid();
        var activation = Signed(_admin, _adminSeed, EventTypes.MetadataActivation,
            JsonSerializer.Serialize(new MetadataActivationPayload(activationId.ToString(), keyId, 1, DateTime.UtcNow.ToString("O"))));

        var early = await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(activation)).Should().ThrowAsync<MetadataKeyMissingException>();
        SyncFailureClassifier.Classify(early.Which).Should().Be(SyncFailureKind.Deferred);
        _node.ProtocolPolicy.SealsMetadata.Should().BeFalse();
        (await CountAsync("tbl_metadata_activation")).Should().Be(0);

        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(keyId, 1))));
        (await _node.EventApplier.ApplyAsync(activation)).Should().Be(EventApplyResult.Applied);

        _node.ProtocolPolicy.SealsMetadata.Should().BeTrue();
        _node.ProtocolPolicy.Declared.Should().Be(4);
        observer.Seen.Should().ContainSingle();
        observer.Seen[0].ActivationId.Should().Be(activationId);
        observer.Seen[0].KeyId.Should().Be(keyId);
        observer.Seen[0].AuthorNodeId.Should().Be(_admin);
        (await CountAsync("tbl_metadata_activation")).Should().Be(1);

        // Redelivered: already in the log, nothing runs again.
        (await _node.EventApplier.ApplyAsync(activation)).Should().Be(EventApplyResult.Applied);
        observer.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task ASecondActivation_ChangesNothing()
    {
        var observer = new RecordingObserver();
        _node.ActivationObservers.Add(observer);
        var keyId = NewKeyId();
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(keyId, 1))));
        var firstId = Guid.NewGuid();
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataActivation,
            JsonSerializer.Serialize(new MetadataActivationPayload(firstId.ToString(), keyId, 1, DateTime.UtcNow.ToString("O")))));
        (await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataActivation,
            JsonSerializer.Serialize(new MetadataActivationPayload(Guid.NewGuid().ToString(), keyId, 1, DateTime.UtcNow.ToString("O"))))))
            .Should().Be(EventApplyResult.Applied, "a race of two activations is not an error");

        using var conn = _node.Factory.CreateConnection();
        (await conn.QuerySingleAsync<string>("SELECT activation_id FROM tbl_metadata_activation")).Should().Be(firstId.ToString("D"));
        observer.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task AnActivation_NamingAnotherGeneration_IsRefused()
    {
        var keyId = NewKeyId();
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(keyId, 1))));
        var evt = Signed(_admin, _adminSeed, EventTypes.MetadataActivation,
            JsonSerializer.Serialize(new MetadataActivationPayload(Guid.NewGuid().ToString(), keyId, 2, DateTime.UtcNow.ToString("O"))));
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(evt)).Should().ThrowAsync<InvalidDataException>();
        _node.ProtocolPolicy.SealsMetadata.Should().BeFalse();
    }

    [Fact]
    public async Task AnActivation_FromAPlainPeer_IsRefused()
    {
        var keyId = NewKeyId();
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(keyId, 1))));
        var evt = Signed(_plain, _plainSeed, EventTypes.MetadataActivation,
            JsonSerializer.Serialize(new MetadataActivationPayload(Guid.NewGuid().ToString(), keyId, 1, DateTime.UtcNow.ToString("O"))));
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(evt)).Should().ThrowAsync<OriginatorNotSuperadminException>();
        _node.ProtocolPolicy.SealsMetadata.Should().BeFalse();
    }

    /// <summary>Parity of the package check with the appliers (review round 3): the key and the activation these appliers write pass <see cref="ControlDbValidator"/>.</summary>
    [Fact]
    public async Task TheRowsTheMetadataAppliersWrite_AreAcceptedByTheControlDbValidator()
    {
        var keyId = NewKeyId();
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataKeySet, JsonSerializer.Serialize(KeyPayload(keyId, 1))));
        await _node.EventApplier.ApplyAsync(Signed(_admin, _adminSeed, EventTypes.MetadataActivation,
            JsonSerializer.Serialize(new MetadataActivationPayload(Guid.NewGuid().ToString(), keyId, 1, DateTime.UtcNow.ToString("O")))));

        var file = Path.Combine(Path.GetTempPath(), "bmb_ctlparity_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Format2TestKit.CopyControlTables(_node.Factory, file);
            ControlDbValidator.Validate(file).Should().Be(_lamport, "the activation is the last event signed");
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static string NewKeyId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static MetadataKeySetPayload KeyPayload(string keyId, int generation) => new(
        keyId, generation, DekFingerprint.Of(RandomNumberGenerator.GetBytes(32)), 0,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
        DateTime.UtcNow.ToString("O"));

    private SyncEvent Signed(Guid node, byte[] seed, string type, string payload, int protocol = 3)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = node, LamportTs = ++_lamport, EventType = type, Payload = payload,
            ProtocolVersion = protocol, CreatedAt = DateTime.UtcNow,
        };
        evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task<(Guid Id, byte[] Seed)> TrustAsync(string name, bool isSuperadmin)
    {
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = pub, Status = "A", IsSuperadmin = isSuperadmin,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return (id, seed);
    }

    private async Task<long> CountAsync(string table)
    {
        using var conn = _node.Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");
    }

    private sealed class RecordingObserver : IMetadataActivationObserver
    {
        public List<MetadataActivation> Seen { get; } = [];
        public Task OnActivatedAsync(MetadataActivation activation, CancellationToken ct = default)
        {
            Seen.Add(activation);
            return Task.CompletedTask;
        }
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
