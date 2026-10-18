using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-307 fix round (F3, F4, F6, F7, F8): the ONE writer of a peer's address, run against a real node (real event log, whitelist and signing) with a fake network.
/// Nothing is sent before the policy has accepted the address, the same canonical address is a no-op, the peer's key must be the one on record, what the loop
/// learned about the old address is forgotten, and the audit line carries fingerprints, never the address.
/// </summary>
public class PeerAddressServiceTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private const string OldAddress = "http://old.local";
    private Fixture _node = null!;
    private readonly Guid _peerId = Guid.NewGuid();
    private readonly Peer _peer;
    private readonly Audit _audit = new();
    private readonly Resetter _resetter = new();
    private PeerAddressService _service = null!;

    public PeerAddressServiceTests() => _peer = new Peer(_peerId);

    public enum Answer { Good, NotFound, OtherNode, OtherKey, NoConnection, Forbidden }

    private sealed class Peer(Guid id) : HttpMessageHandler
    {
        public Answer Mode { get; set; } = Answer.Good;
        public List<string> Urls { get; } = [];

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(Mode switch
            {
                Answer.NotFound => new HttpResponseMessage(HttpStatusCode.NotFound),
                Answer.Forbidden => new HttpResponseMessage(HttpStatusCode.Forbidden),
                Answer.NoConnection => throw new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)),
                Answer.OtherNode => Json(new { nodeId = Guid.NewGuid(), displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = 3 }),
                Answer.OtherKey => Json(new
                {
                    nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()), protocolVersion = 3,
                }),
                _ => Json(new { nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = 3 }),
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastName { get; private set; }
        public HttpClient CreateClient(string name) { LastName = name; return new HttpClient(handler, disposeHandler: false); }
    }

    private sealed class Audit : IAuditLogRepository
    {
        public List<(string Type, string Id, string Action, string Actor, string Details)> Lines { get; } = [];
        public Task LogAsync(string entityType, string entityId, string action, string actorType, string details)
        {
            lock (Lines) Lines.Add((entityType, entityId, action, actorType, details));
            return Task.CompletedTask;
        }
        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class Resetter : IPeerStateResetter
    {
        public List<Guid> Changed { get; } = [];
        public void PeerChanged(Guid nodeId) { lock (Changed) Changed.Add(nodeId); }
    }

    private Factory _factory = null!;

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Laptop", Ed25519PublicKey = new byte[32], ApiAddress = OldAddress,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _factory = new Factory(_peer);
        _service = new PeerAddressService(_node.WhitelistRepo, _node.NodeRepo, _node.EventLogger, _audit, _factory, NullLogger<PeerAddressService>.Instance, _resetter);
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    private async Task<int> UpdateEventsAsync() =>
        (await _node.EventLogRepo.GetRecentAsync(500, 0, EventTypes.WhitelistUpdate)).Count;

    private async Task<string?> AddressAsync() => (await _node.WhitelistRepo.GetByNodeIdAsync(_peerId))!.ApiAddress;

    // ---------------------------------------------------------------- the change

    [Fact]
    public async Task A_good_address_is_stored_canonical_published_once_audited_by_fingerprint_and_the_loop_is_told()
    {
        var events = await UpdateEventsAsync();

        var result = await _service.ChangeAsync(_peerId, "  HTTP://Peer.Local:80/  ", "7", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.Changed);
        (await AddressAsync()).Should().Be("http://peer.local");
        result.Entry!.ApiAddress.Should().Be("http://peer.local");
        (await UpdateEventsAsync()).Should().Be(events + 1, "one whitelist_update tells the other nodes");
        var published = (await _node.EventLogRepo.GetRecentAsync(1, 0, EventTypes.WhitelistUpdate)).Single();
        published.Payload.Should().Contain("http://peer.local").And.NotContain("HTTP://").And.NotContain("old.local");
        (await _node.WhitelistRepo.GetByNodeIdAsync(_peerId))!.LamportTs.Should().Be(published.LamportTs, "the row carries the version the event minted");
        _resetter.Changed.Should().Equal(_peerId);
        _factory.LastName.Should().Be(PeerAddressService.HttpClientName, "the probe uses its own pinned, no-redirect client");

        var line = _audit.Lines.Should().ContainSingle().Which;
        line.Action.Should().Be("peer_address_changed");
        line.Type.Should().Be("whitelist");
        line.Id.Should().Be(_peerId.ToString());
        line.Actor.Should().Be("web");
        line.Details.Should().Contain("by 7").And.Contain(PeerAddressPolicy.Fingerprint("http://old.local")).And.Contain(PeerAddressPolicy.Fingerprint("http://peer.local"));
        line.Details.Should().NotContain("old.local").And.NotContain("peer.local");
    }

    [Fact]
    public async Task The_same_canonical_address_is_a_no_op_nothing_probed_published_audited_or_reset()
    {
        var events = await UpdateEventsAsync();

        var same = await _service.ChangeAsync(_peerId, "HTTP://OLD.LOCAL:80/", "7", "web");

        same.Outcome.Should().Be(PeerAddressOutcome.Unchanged);
        same.Succeeded.Should().BeTrue();
        _peer.Urls.Should().BeEmpty("there is nothing to check");
        (await UpdateEventsAsync()).Should().Be(events);
        _audit.Lines.Should().BeEmpty();
        _resetter.Changed.Should().BeEmpty();

        // and after a real change, the second request for it is the no-op
        (await _service.ChangeAsync(_peerId, "peer.local", "7", "web")).Outcome.Should().Be(PeerAddressOutcome.Changed);
        events = await UpdateEventsAsync();
        (await _service.ChangeAsync(_peerId, "https://peer.local/", "7", "web")).Outcome.Should().Be(PeerAddressOutcome.Unchanged);
        (await UpdateEventsAsync()).Should().Be(events);
        _audit.Lines.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- the policy comes first

    [Theory]
    [InlineData("http://user:pw@peer.local")]
    [InlineData("https://peer.local/?token=abc")]
    [InlineData("ftp://peer.local")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[fe80::1]:5300")]
    [InlineData("http://0.0.0.0:5300")]
    [InlineData("http://metadata.google.internal")]
    [InlineData("")]
    public async Task A_refused_address_sends_nothing_anywhere_and_changes_nothing(string typed)
    {
        var events = await UpdateEventsAsync();

        var result = await _service.ChangeAsync(_peerId, typed, "7", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.Refused);
        result.Message.Should().NotContain("pw").And.NotContain("abc").And.NotContain("peer.local");
        _peer.Urls.Should().BeEmpty("the address is judged before any request is made - that is the point of the policy");
        (await AddressAsync()).Should().Be(OldAddress);
        (await UpdateEventsAsync()).Should().Be(events);
        _audit.Lines.Should().BeEmpty();
        _resetter.Changed.Should().BeEmpty();
    }

    [Fact]
    public async Task Loopback_and_private_addresses_are_accepted_when_a_sync_server_answers_there()
    {
        foreach (var lan in new[] { "http://127.0.0.1:5300", "http://192.168.1.20:5300", "http://localhost:5300" })
            (await _service.ChangeAsync(_peerId, lan, "7", "web")).Outcome.Should().Be(PeerAddressOutcome.Changed, lan);
    }

    // ---------------------------------------------------------------- what answers there

    [Fact]
    public async Task The_right_node_id_with_another_key_is_refused_and_nothing_is_written()
    {
        _peer.Mode = Answer.OtherKey;
        var events = await UpdateEventsAsync();

        var result = await _service.ChangeAsync(_peerId, "http://peer.local", "7", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.WrongKey);
        result.Message.Should().Contain("another key").And.NotContain("peer.local");
        (await AddressAsync()).Should().Be(OldAddress);
        (await UpdateEventsAsync()).Should().Be(events);
        _audit.Lines.Should().BeEmpty();
        (await _node.WhitelistRepo.GetByNodeIdAsync(_peerId))!.Ed25519PublicKey.Should().Equal(new byte[32], "the key on record never changes through an address change");
    }

    [Fact]
    public async Task Another_node_id_is_refused()
    {
        _peer.Mode = Answer.OtherNode;

        var result = await _service.ChangeAsync(_peerId, "http://peer.local", "7", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.WrongNode);
        result.Message.Should().Contain("different node").And.NotContain("peer.local");
        (await AddressAsync()).Should().Be(OldAddress);
    }

    [Fact]
    public async Task An_address_that_is_not_a_sync_server_is_refused_with_the_shared_words_and_no_address()
    {
        _peer.Mode = Answer.NotFound;

        var result = await _service.ChangeAsync(_peerId, "http://peer.local", "7", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.NoSyncServer);
        result.Message.Should().StartWith("No sync server at this address:").And.Contain("it answered 404").And.NotContain("peer.local");
        (await AddressAsync()).Should().Be(OldAddress);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_is_refused_unless_the_person_chose_to_save_it_anyway()
    {
        _peer.Mode = Answer.NoConnection;

        var refused = await _service.ChangeAsync(_peerId, "http://peer.local", "7", "web");
        refused.Outcome.Should().Be(PeerAddressOutcome.NoAnswer);
        refused.Message.Should().Contain("connection refused").And.NotContain("peer.local");
        (await AddressAsync()).Should().Be(OldAddress);

        var saved = await _service.ChangeAsync(_peerId, "http://peer.local", "phone", "phone", saveWhenUnanswered: true);
        saved.Outcome.Should().Be(PeerAddressOutcome.Changed);
        (await AddressAsync()).Should().Be("http://peer.local");
        _audit.Lines.Should().ContainSingle().Which.Details.Should().Contain("saved without an answer from it");
        _audit.Lines[0].Actor.Should().Be("phone");
    }

    [Fact]
    public async Task An_address_another_peer_has_is_refused()
    {
        var other = Guid.NewGuid();
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = other, DisplayName = "Desktop", Ed25519PublicKey = new byte[32], ApiAddress = "http://Peer.Local:80/",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _service.ChangeAsync(_peerId, "http://peer.local", "7", "web");

        result.Outcome.Should().Be(PeerAddressOutcome.InUse);
        result.Message.Should().Contain("Desktop").And.NotContain("peer.local");
    }

    [Fact]
    public async Task An_unknown_peer_and_this_node_itself_are_refused()
    {
        (await _service.ChangeAsync(Guid.NewGuid(), "http://peer.local", "7", "web")).Outcome.Should().Be(PeerAddressOutcome.NotFound);
        var self = (await _node.NodeRepo.GetAsync())!.NodeId;
        (await _service.ChangeAsync(self, "http://peer.local", "7", "web")).Outcome.Should().BeOneOf(PeerAddressOutcome.OwnNode, PeerAddressOutcome.NotFound);
    }

    // ---------------------------------------------------------------- F7: what the loop learned about the old address is forgotten

    [Fact]
    public async Task With_the_real_loop_an_address_change_forgets_the_failure_streak_of_the_old_address()
    {
        var contacts = new PeerContactLog();
        var client = new SyncClient(_node.NodeRepo, _node.EventLogRepo, new BeeMemoryBank.Storage.Sqlite.SyncPositionRepository(_node.Factory),
            new BeeMemoryBank.Storage.Sqlite.SyncPushPositionRepository(_node.Factory), _node.EventApplier, new SessionNodeAuthSigner(_node.Session),
            NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(), _node.QuarantineRepo, new BeeMemoryBank.Storage.Sqlite.BlobRepository(_node.Factory),
            whitelistRepo: _node.WhitelistRepo);
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(client).AddSingleton(contacts)
            .BuildServiceProvider();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SyncScheduler>.Instance, new SyncTrigger(), _factory);
        var service = new PeerAddressService(_node.WhitelistRepo, _node.NodeRepo, _node.EventLogger, _audit, _factory, NullLogger<PeerAddressService>.Instance, scheduler);

        _peer.Mode = Answer.Forbidden;
        await scheduler.SyncAllAsync(CancellationToken.None);
        contacts.Get(_peerId, OldAddress).OutboundFailure.Should().NotBeNull("the old address is failing");

        _peer.Mode = Answer.Good;
        (await service.ChangeAsync(_peerId, "http://peer.local", "7", "web")).Outcome.Should().Be(PeerAddressOutcome.Changed);

        contacts.Get(_peerId, "http://peer.local").OutboundFailure.Should().BeNull("a failure of the old address is not shown for the new one");
        contacts.Get(_peerId).OutboundFailure.Should().BeNull("and the loop forgot it altogether");
    }
}
