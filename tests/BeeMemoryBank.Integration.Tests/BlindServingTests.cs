using System.Net;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What a blind node serves and keeps on its own: the replica for an Android blind node (plan 10),
/// signed with its file key, and local log trimming without an event (plan 5.4).
/// </summary>
public class BlindServingTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _blind = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _blind.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Replica_ForABlindPeer_IsABlindPackageSignedWithTheFileKey()
    {
        var self = await IdentityAsync();
        var android = BlindNodeId.NewId();
        await WhitelistAsync(android, Ed25519Signer.GenerateKeyPair().publicKey);
        using var http = PeerClient(android);

        var resp = await http.GetAsync("/api/blind/replica");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var file = Path.Combine(_blind.DataPath, "replica-received.tar.gz");
        await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
        var package = await _blind.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(file, Path.Combine(_blind.DataPath, "replica-extracted"));
        package.IsSignedBy(self.Ed25519PublicKey).Should().BeTrue("a blind node signs with the key it keeps in its file");
        BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(package.Directory, BlindManifest.FileName)))
            .ProducerNodeId.Should().Be(self.NodeId);
    }

    /// <summary>
    /// Review L-merge #3: an Android blind node replicating from this one — its only hub — can dial it
    /// and pins its certificate: the producer row carries the address and the pin.
    /// </summary>
    [Fact]
    public async Task Replica_NamesWhereToReachTheProducer_AndItsPin()
    {
        var self = await IdentityAsync();
        var android = BlindNodeId.NewId();
        await WhitelistAsync(android, Ed25519Signer.GenerateKeyPair().publicKey);
        using var http = PeerClient(android);

        var resp = await http.GetAsync("/api/blind/replica");

        var file = Path.Combine(_blind.DataPath, "replica-received.tar.gz");
        await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
        var package = await _blind.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(file, Path.Combine(_blind.DataPath, "replica-extracted"));
        var producer = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(package.Directory, BlindManifest.FileName)))
            .Whitelist.Single(p => p.NodeId == self.NodeId);
        producer.ApiAddress.Should().Be(BlindNodeFactory.PublicAddress);
        producer.TlsSpki.Should().Be(_blind.Services.GetRequiredService<BlindTlsIdentity>().Spki);
        producer.IsSuperadmin.Should().BeFalse("a blind node is never a reseed authority");
    }

    [Fact]
    public async Task Replica_ForAFullPeer_IsRefused()
    {
        await IdentityAsync();
        var phone = Guid.NewGuid();
        await WhitelistAsync(phone, Ed25519Signer.GenerateKeyPair().publicKey);
        using var http = PeerClient(phone);

        (await http.GetAsync("/api/blind/replica")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Plan 5.4: rows every peer has read and the last anchor covers go; a compaction record at the
    /// cut makes a peer below it get the ordinary 410. Without an anchor nothing goes — the events
    /// after the last anchor are what a restore checks by signature.
    /// </summary>
    [Fact]
    public async Task Trim_CutsWhatAllPeersReadAndTheAnchorCovers_AndAnswers410Below()
    {
        await IdentityAsync();
        var (author, key) = (Guid.NewGuid(), Ed25519Signer.GenerateKeyPair());
        await WhitelistAsync(author, key.publicKey);
        var reader = Guid.NewGuid();
        await WhitelistAsync(reader, Ed25519Signer.GenerateKeyPair().publicKey);
        using (var scope = _blind.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            for (var lamport = 1; lamport <= 6; lamport++)
                await applier.ApplyAsync(Signed(author, key.privateKey, lamport));
        }
        var pushed = _blind.Services.GetRequiredService<ISyncPushPositionRepository>();
        // Reported, not delivered (Codex round 2, security #3): the trimmer cuts at what a peer
        // acknowledged, so these are the numbers it reads — and the author was served further than
        // it reported, which must not count for anything.
        await pushed.RecordReportedPositionAsync(author, 2);
        await pushed.UpdatePositionAsync(author, 5);
        await pushed.RecordReportedPositionAsync(reader, 4);
        await pushed.UpdatePositionAsync(reader, 4);
        var trimmer = _blind.Services.GetRequiredService<BlindLogTrimmer>();

        (await trimmer.TrimAsync()).Should().BeNull("with no state anchor nothing is covered yet");

        using (var conn = _blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_state_anchor (anchor_id, author_node_id, dek_fingerprint, position_vector, digest, hmac, created_at, lamport_ts)
                  VALUES ('a1', @author, 'fp', @vector, 'd', 'h', @at, 10)",
                new { author = author.ToString(), vector = $"{{\"{author}\": 3}}", at = DateTime.UtcNow.ToString("O") });

        (await trimmer.TrimAsync()).Should().Be(2,
            "the anchor covers up to the third event, one peer acknowledged two and the other four — and what the first was merely served (5) counts for nothing");
        var events = _blind.Services.GetRequiredService<IEventLogRepository>();
        (await events.GetMinSequenceAsync()).Should().Be(3);
        using var http = PeerClient(reader);
        (await http.GetAsync("/api/sync/events?afterSequence=1")).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await http.GetAsync("/api/sync/events?afterSequence=2")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private HttpClient PeerClient(Guid peer)
    {
        var http = _blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", _blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, BeeMemoryBank.Sync.SyncProtocolVersion.Current));
        return http;
    }

    private Task WhitelistAsync(Guid id, byte[] key) =>
        _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "peer", Ed25519PublicKey = key, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    private static SyncEvent Signed(Guid author, byte[] key, long lamport)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = "some_future_type",
            Payload = "{}", ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(key, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task<NodeIdentity> IdentityAsync() =>
        (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
}
