using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-307: an address that answers 404 (or with a page that is not a sync identity) is said to have "no sync server at this address" - by the shared
/// client, the scheduler's result (the phone's heartbeat reads it) and the contact log - and never as a cycle that ended well ("ok (applied 0)"). And the
/// phone, which syncs through the same client, declares protocol 4 with the envelope capability and its version once it holds encrypted metadata.
/// </summary>
public class NoSyncServerTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _node = null!;
    private SyncProtocolPolicy _policy = null!;
    private SyncClient _client = null!;
    private readonly Guid _peerId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        _policy = new SyncProtocolPolicy(_node.Factory);
        _client = new SyncClient(_node.NodeRepo, _node.EventLogRepo, new SyncPositionRepository(_node.Factory),
            new SyncPushPositionRepository(_node.Factory), _node.EventApplier, new SessionNodeAuthSigner(_node.Session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(),
            _node.QuarantineRepo, new BlobRepository(_node.Factory), blindState: new BlindState(_node.Factory), protocolPolicy: _policy);
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    public enum Answer { Good, NotFound, MethodNotAllowed, Gone, Moved, WebPage, OtherJson, EmptyIdentity, ShutDoor, Forbidden, InTrouble, NoConnection }

    /// <summary>A fake peer: speaks sync when it is Good, and is a missing page, a web page, another service, a shut door or silent when it is not.</summary>
    private sealed class Peer(Guid id) : HttpMessageHandler
    {
        public Answer Mode { get; set; } = Answer.Good;
        public int Requests { get; private set; }
        public string? AuthenticateBody { get; private set; }
        public string? ReportPositionQuery { get; private set; }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/sync/identity"))
            {
                switch (Mode)
                {
                    case Answer.NotFound: return new(HttpStatusCode.NotFound);
                    case Answer.MethodNotAllowed: return new(HttpStatusCode.MethodNotAllowed);
                    case Answer.Gone: return new(HttpStatusCode.Gone);
                    case Answer.Moved: return new(HttpStatusCode.Found);
                    case Answer.WebPage:
                        return new(HttpStatusCode.OK) { Content = new StringContent("<html><body>Welcome to my router</body></html>", Encoding.UTF8, "text/html") };
                    case Answer.OtherJson: return Json(new { status = "ok", service = "something else" });
                    case Answer.EmptyIdentity: return Json(new { nodeId = Guid.Empty, displayName = "", ed25519PublicKeyB64 = "" });
                    case Answer.ShutDoor: return new(HttpStatusCode.Unauthorized);
                    case Answer.Forbidden: return new(HttpStatusCode.Forbidden);
                    case Answer.InTrouble: return new(HttpStatusCode.ServiceUnavailable);
                    case Answer.NoConnection:
                        throw new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
                }
            }
            if (path.EndsWith("/api/sync/authenticate")) AuthenticateBody = await request.Content!.ReadAsStringAsync(ct);
            if (path.EndsWith("/api/sync/report-position")) ReportPositionQuery = request.RequestUri.Query;
            return path.EndsWith("/api/sync/sentinel") ? new(HttpStatusCode.NotFound)
                : path.EndsWith("/api/sync/identity") ? Json(new { nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = SyncProtocolVersion.Current, capabilities = MetadataCapabilities.Advertised })
                : path.EndsWith("/api/sync/challenge") ? Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = id })
                : path.EndsWith("/api/sync/authenticate") ? Json(new { token = "t" })
                : path.EndsWith("/api/sync/report-position") ? new(HttpStatusCode.OK)
                : path.EndsWith("/api/sync/events") && request.Method == HttpMethod.Get ? Json(Array.Empty<object>())
                : path.EndsWith("/api/sync/events") ? Json(new { applied = 0, skipped = 0, lastAppliedSequence = (long?)null, dropped = 0 })
                : new(HttpStatusCode.NotFound);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // ---------------------------------------------------------------- the identity page

    [Theory]
    [InlineData(Answer.NotFound, "it answered 404 Not Found")]
    [InlineData(Answer.MethodNotAllowed, "it answered 405")]
    [InlineData(Answer.Gone, "it answered 410")]
    [InlineData(Answer.Moved, "it answered 302")]
    [InlineData(Answer.WebPage, "not with a sync identity")]
    [InlineData(Answer.OtherJson, "not with a sync identity")]
    [InlineData(Answer.EmptyIdentity, "not with a sync identity")]
    public async Task An_address_that_is_not_a_sync_server_says_so(Answer answer, string reason)
    {
        var peer = new Peer(_peerId) { Mode = answer };

        var act = () => SyncAddress.ReadIdentityAsync(new HttpClient(peer), "http://peer.local/");

        var thrown = (await act.Should().ThrowAsync<NoSyncServerException>()).Which;
        thrown.Message.Should().StartWith("No sync server at this address: ", "the address is not named: it may carry a user name or a token");
        thrown.Message.Should().NotContain("peer.local");
        thrown.Message.Should().Contain(reason);
        thrown.Reason.Should().NotContain("peer.local", "what the status pages show keeps addresses out");
    }

    [Theory]
    [InlineData(Answer.ShutDoor, HttpStatusCode.Unauthorized)]
    [InlineData(Answer.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(Answer.InTrouble, HttpStatusCode.ServiceUnavailable)]
    public async Task A_shut_door_or_a_server_in_trouble_is_a_server_and_stays_an_http_failure(Answer answer, HttpStatusCode status)
    {
        var peer = new Peer(_peerId) { Mode = answer };

        var act = () => SyncAddress.ReadIdentityAsync(new HttpClient(peer), "http://peer.local");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task No_connection_is_no_answer_not_no_sync_server()
    {
        var peer = new Peer(_peerId) { Mode = Answer.NoConnection };

        var act = () => SyncAddress.ReadIdentityAsync(new HttpClient(peer), "http://peer.local");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Check_gives_a_verdict_for_a_screen_that_validates_an_address()
    {
        var peer = new Peer(_peerId);
        using var http = new HttpClient(peer);

        var good = await SyncAddress.CheckAsync(http, "http://peer.local");
        peer.Mode = Answer.NotFound;
        var missing = await SyncAddress.CheckAsync(http, "http://peer.local");
        peer.Mode = Answer.NoConnection;
        var silent = await SyncAddress.CheckAsync(http, "http://peer.local");

        good.Verdict.Should().Be(AddressVerdict.SyncServer);
        good.Identity!.NodeId.Should().Be(_peerId);
        missing.Verdict.Should().Be(AddressVerdict.NoSyncServer);
        missing.Message.Should().StartWith("No sync server at this address");
        silent.Verdict.Should().Be(AddressVerdict.NoAnswer);
        silent.Message.Should().Contain("no answer").And.Contain("connection refused");
    }

    [Fact]
    public async Task The_sync_client_fails_with_the_same_error_instead_of_a_bare_404()
    {
        var peer = new Peer(_peerId) { Mode = Answer.NotFound };
        using var http = new HttpClient(peer);

        var act = () => _client.SyncWithAsync(http, "http://peer.local", _peerId);

        await act.Should().ThrowAsync<NoSyncServerException>();
    }

    // ---------------------------------------------------------------- the cycle

    private async Task<(SyncScheduler Scheduler, Peer Peer, PeerContactLog Log)> NodeAsync()
    {
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Laptop", Ed25519PublicKey = new byte[32], ApiAddress = "http://peer.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var peer = new Peer(_peerId);
        var log = new PeerContactLog();
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client).AddSingleton(log)
            .BuildServiceProvider();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncScheduler>.Instance, new SyncTrigger(), new Factory(peer));
        return (scheduler, peer, log);
    }

    [Fact]
    public async Task A_cycle_with_a_peer_that_has_no_sync_server_is_not_a_clean_cycle()
    {
        var (scheduler, peer, log) = await NodeAsync();
        peer.Mode = Answer.NotFound;

        var result = await scheduler.SyncAllAsync(CancellationToken.None);

        result.TotalApplied.Should().Be(0);
        result.Error.Should().BeNull("the cycle itself ran; it is the peer that was not served");
        result.PeerFailures.Should().ContainSingle().Which.Should().Be(
            new PeerSyncFailure(_peerId, "Laptop", "no sync server at this address: it answered 404 Not Found"));
        result.Problem.Should().Be("Laptop: no sync server at this address: it answered 404 Not Found",
            "this is the text the phone's heartbeat shows after \"error:\", where \"ok (applied 0)\" used to stand");
        log.Get(_peerId).OutboundFailure.Should().Be("no sync server at this address: it answered 404 Not Found");
    }

    [Fact]
    public async Task The_cycles_while_the_peer_is_left_alone_still_say_it_is_not_served_and_a_good_session_ends_it()
    {
        var (scheduler, peer, _) = await NodeAsync();
        peer.Mode = Answer.NotFound;
        await scheduler.SyncAllAsync(CancellationToken.None);
        var asked = peer.Requests;

        var next = await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().Be(asked, "a wrong address is paused like an absent peer: one warning, then growing pauses");
        next.Problem.Should().Contain("no sync server at this address", "a paused peer is still not served; \"ok (applied 0)\" would hide it");

        peer.Mode = Answer.Good;
        (await scheduler.CheckPeerNowAsync(_peerId)).Answered.Should().BeTrue();
        var healed = await scheduler.SyncAllAsync(CancellationToken.None);
        healed.Problem.Should().BeNull();
        healed.PeerFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cycle_that_reaches_every_peer_is_clean()
    {
        var (scheduler, _, _) = await NodeAsync();

        var result = await scheduler.SyncAllAsync(CancellationToken.None);

        result.Problem.Should().BeNull();
    }

    [Fact]
    public void The_problem_of_a_cycle_is_its_own_error_or_what_each_peer_said()
    {
        new SyncCycleResult(0).Problem.Should().BeNull();
        new SyncCycleResult(0, "the whitelist could not be read").Problem.Should().Be("the whitelist could not be read");
        new SyncCycleResult(2, null, [new PeerSyncFailure(Guid.NewGuid(), "A", "no answer"), new PeerSyncFailure(Guid.NewGuid(), "B", "it answered 503")])
            .Problem.Should().Be("A: no answer; B: it answered 503");
    }

    // ---------------------------------------------------------------- what the phone shows the node

    [Fact]
    public async Task A_node_declares_protocol_3_before_encrypted_metadata_and_4_after_with_the_capability_and_its_version()
    {
        var peer = new Peer(_peerId);
        using var http = new HttpClient(peer);

        await _client.SyncWithAsync(http, "http://peer.local", _peerId);
        var before = (Authenticate: JsonDocument.Parse(peer.AuthenticateBody!).RootElement.Clone(), Query: peer.ReportPositionQuery!);
        _policy.MarkActivated();
        await _client.SyncWithAsync(http, "http://peer.local", _peerId);
        var after = (Authenticate: JsonDocument.Parse(peer.AuthenticateBody!).RootElement.Clone(), Query: peer.ReportPositionQuery!);

        before.Authenticate.GetProperty("protocolVersion").GetInt32().Should().Be(SyncProtocolVersion.Current);
        after.Authenticate.GetProperty("protocolVersion").GetInt32().Should().Be(EventClasses.SealedProtocol).And.Be(4);
        foreach (var body in new[] { before.Authenticate, after.Authenticate })
        {
            body.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).Should().Contain("metadata-envelope-v1");
            body.GetProperty("appVersion").GetString().Should().Be(BuildVersion.Current).And.NotBeNullOrWhiteSpace().And.NotBe("0.0.0");
        }
        after.Query.Should().Contain("protocolVersion=4").And.Contain("capabilities=metadata-envelope-v1")
            .And.Contain("appVersion=" + Uri.EscapeDataString(BuildVersion.Current));
    }

    [Fact]
    public void The_phones_composition_has_what_an_activated_network_sends_it()
    {
        // MauiProgram composes AddStorage().AddCore().AddSync(): the protocol policy, the envelope opener, the held-event drain, the key ring and the
        // activation service come with AddSync, so a phone opens encrypted metadata with the same code a desktop does. (Registrations are checked, not
        // resolved: resolving needs the whole storage and core of a node.)
        var services = new ServiceCollection().AddLogging();
        services.AddSync();

        foreach (var type in new[]
        {
            typeof(ISyncProtocolPolicy), typeof(IEnvelopeOpener), typeof(IHeldEventDrain),
            typeof(BeeMemoryBank.Vault.Metadata.IMetadataKeyRing), typeof(BeeMemoryBank.Vault.Metadata.MetadataActivationService),
            typeof(BeeMemoryBank.Vault.Metadata.MetadataReadinessService),
        })
            services.Should().Contain(d => d.ServiceType == type, $"{type.Name} is part of what AddSync gives every full node, the phone included");
        MetadataCapabilities.Advertised.Should().Contain("metadata-envelope-v1");
    }
}
