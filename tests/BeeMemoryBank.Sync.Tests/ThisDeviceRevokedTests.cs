using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Release 2.12, "this device was disconnected": the client side. A peer's 401 that carries its signed "your row is revoked" refusal becomes a
/// <see cref="ThisDeviceRevokedException"/> only when the signature verifies under the key on record and is bound to this node and this challenge;
/// the sync loop of any node then says it once and asks that peer again only every <see cref="SyncScheduler.ClosedRetry"/>, in the calm words of
/// <see cref="PeerContactLog.Describe"/>, instead of a warning a minute.
/// </summary>
public class ThisDeviceRevokedTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _node = null!;
    private SyncClient _client = null!;

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        _client = new SyncClient(_node.NodeRepo, _node.EventLogRepo, new SyncPositionRepository(_node.Factory),
            new SyncPushPositionRepository(_node.Factory), _node.EventApplier, new SessionNodeAuthSigner(_node.Session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(),
            _node.QuarantineRepo, new BlobRepository(_node.Factory),
            blindState: new BlindState(_node.Factory), whitelistRepo: _node.WhitelistRepo);
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    /// <summary>
    /// A full node that answers the identity and the challenge, and refuses the authentication with a 401: plain, or with its refusal signed by
    /// <see cref="SigningKey"/> (its own key, or another one) for <see cref="ProofFor"/> (this node, or another one).
    /// </summary>
    private sealed class RevokingPeer(Guid id, byte[] publicKey, byte[] privateKey) : HttpMessageHandler
    {
        public byte[] SigningKey { get; set; } = privateKey;
        public Guid? ProofFor { get; set; }
        public bool SignedRefusal { get; set; } = true;
        public int Authentications;
        private byte[] _challenge = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sync/identity")
                return Json(HttpStatusCode.OK, new
                {
                    nodeId = id, displayName = "Office", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
                    protocolVersion = SyncProtocolVersion.Current, capabilities = MetadataCapabilities.Advertised,
                });
            if (path == "/api/sync/challenge")
            {
                _challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                return Json(HttpStatusCode.OK, new { challenge = Convert.ToBase64String(_challenge), serverNodeId = id });
            }
            if (path == "/api/sync/authenticate")
            {
                Interlocked.Increment(ref Authentications);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var caller = (body.TryGetProperty("nodeId", out var n) ? n : body.GetProperty("NodeId")).GetGuid();
                if (!SignedRefusal) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var proof = Ed25519Signer.Sign(SigningKey, DeviceRevokedProof.Payload(id, ProofFor ?? caller, _challenge));
                return Json(HttpStatusCode.Unauthorized, new { refusal = DeviceRevokedProof.Refusal, proofB64 = Convert.ToBase64String(proof) });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body) };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger : ILogger<SyncScheduler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private async Task<(Guid PeerId, RevokingPeer Peer)> PeerAsync()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Office", Ed25519PublicKey = pub, ApiAddress = "http://office.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (id, new RevokingPeer(id, pub, priv));
    }

    private async Task<Exception> SessionFailureAsync(Guid peerId, RevokingPeer peer)
    {
        using var http = new HttpClient(peer, disposeHandler: false);
        var act = () => _client.SyncWithPeerAsync(http, "http://office.local", peerId, CancellationToken.None);
        return (await act.Should().ThrowAsync<Exception>()).Which;
    }

    [Fact]
    public async Task A_refusal_signed_by_the_peer_for_this_node_and_this_challenge_is_a_proven_revocation()
    {
        var (peerId, peer) = await PeerAsync();

        var thrown = await SessionFailureAsync(peerId, peer);

        thrown.Should().BeOfType<ThisDeviceRevokedException>().Which.PeerNodeId.Should().Be(peerId);
        ((HttpRequestException)thrown).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_refusal_signed_by_another_key_is_a_plain_refusal()
    {
        var (peerId, peer) = await PeerAsync();
        peer.SigningKey = Ed25519Signer.GenerateKeyPair().privateKey;

        var thrown = await SessionFailureAsync(peerId, peer);

        thrown.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_refusal_signed_for_another_node_is_a_plain_refusal()
    {
        var (peerId, peer) = await PeerAsync();
        peer.ProofFor = Guid.NewGuid();

        var thrown = await SessionFailureAsync(peerId, peer);

        thrown.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_plain_401_stays_a_plain_refusal()
    {
        var (peerId, peer) = await PeerAsync();
        peer.SignedRefusal = false;

        var thrown = await SessionFailureAsync(peerId, peer);

        thrown.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void A_proof_does_not_verify_for_another_challenge()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var (server, caller) = (Guid.NewGuid(), Guid.NewGuid());
        var challenge = new byte[32];
        var proof = Ed25519Signer.Sign(priv, DeviceRevokedProof.Payload(server, caller, challenge));

        DeviceRevokedProof.Verify(pub, server, caller, challenge, proof).Should().BeTrue();
        DeviceRevokedProof.Verify(pub, server, caller, [.. challenge[..31], 1], proof).Should().BeFalse("a replayed refusal for an older challenge");
        DeviceRevokedProof.Verify(pub, caller, server, challenge, proof).Should().BeFalse();
        DeviceRevokedProof.Verify(null, server, caller, challenge, proof).Should().BeFalse();
        DeviceRevokedProof.Verify(pub, server, caller, challenge, new byte[63]).Should().BeFalse();
    }

    [Theory]
    [InlineData("full")]
    [InlineData("blind")]
    public async Task The_sync_loop_says_it_once_and_asks_again_only_every_ClosedRetry(string role)
    {
        var (peerId, peer) = await PeerAsync();
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client)
            .AddSingleton<INodeRole>(new EnvironmentNodeRole(role)).AddSingleton<PeerContactLog>();
        var provider = services.BuildServiceProvider();
        var log = new ListLogger();
        var scheduler = new SyncScheduler(provider.GetRequiredService<IServiceScopeFactory>(), log, new SyncTrigger(), new Factory(peer));

        SyncCycleResult? last = null;
        for (var cycle = 0; cycle < 30; cycle++)
            last = await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Authentications.Should().Be(1, "the first cycle asks once; the next ones leave the peer alone for the retry interval");
        var warnings = log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        warnings.Should().ContainSingle().Which.Message.Should().Contain("an administrator disconnected this node");
        last!.PeerFailures.Should().ContainSingle().Which.Reason.Should().Contain("an administrator disconnected this device from the bank");
        provider.GetRequiredService<PeerContactLog>().Get(peerId).OutboundFailure.Should().Contain("stays readable here");
    }

    [Fact]
    public async Task When_the_interval_has_passed_it_asks_again_and_the_same_episode_stays_one_line()
    {
        var (_, peer) = await PeerAsync();
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client);
        var log = new ListLogger();
        var scheduler = new SyncScheduler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), log, new SyncTrigger(), new Factory(peer));
        scheduler.ClosedRetry = TimeSpan.Zero;   // "the interval has passed" at every cycle

        for (var cycle = 0; cycle < 4; cycle++)
            await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Authentications.Should().Be(4);
        log.Entries.Count(e => e.Level >= LogLevel.Warning).Should().Be(1, "the same episode: debug lines, not a warning a cycle");
    }
}
