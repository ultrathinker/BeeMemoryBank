using System.Net;
using System.Net.Http.Headers;
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
/// BMB-307 fix round. What the first round tested with a bare handler is tested again through the REAL pinned handler (<see cref="SpkiPinGuardHandler"/>, which
/// is what every sync client and the address probe are built on), with the answers a real network gives: a redirect to a login page, a 502 and 504 of a proxy,
/// a captive portal, a host that never answers, a body that is endless or trickles; and a protocol-3 peer without the envelope capability, which must be a failure
/// at every layer (F1, F2, F5, F9, F11).
/// </summary>
public class NoSyncServerFixRoundTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private const string Secret = "s3cret-token";
    private Fixture _node = null!;
    private readonly Guid _peerId = Guid.NewGuid();
    private SyncProtocolPolicy _policy = null!;
    private SyncClient _client = null!;

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
            _node.QuarantineRepo, new BlobRepository(_node.Factory), blindState: new BlindState(_node.Factory), protocolPolicy: _policy,
            whitelistRepo: _node.WhitelistRepo);
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    public enum Answer
    {
        Good, RedirectToLogin, BadGateway, GatewayTimeout, CaptivePortal, Hang, Oversize, Endless, Trickle, Protocol3NoCapability, OtherKey,
    }

    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)' ', offset, count); Served += count; return count; }
        public override int Read(Span<byte> buffer) { buffer.Fill((byte)' '); Served += buffer.Length; return buffer.Length; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A few bytes, then nothing for ever (until cancelled).</summary>
    private sealed class TrickleStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent) { _sent = true; buffer.Span[0] = (byte)'{'; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Peer(Guid id) : HttpMessageHandler
    {
        public Answer Mode { get; set; } = Answer.Good;
        public EndlessStream? Endless { get; private set; }
        public int Requests;

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/sync/identity"))
            {
                switch (Mode)
                {
                    case Answer.RedirectToLogin:
                        return new(HttpStatusCode.Found) { Headers = { Location = new Uri("http://login.local/portal?token=" + Secret) } };
                    case Answer.BadGateway: return new(HttpStatusCode.BadGateway);
                    case Answer.GatewayTimeout: return new(HttpStatusCode.GatewayTimeout);
                    case Answer.CaptivePortal: return new(HttpStatusCode.NetworkAuthenticationRequired);
                    case Answer.Hang:
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case Answer.Oversize:
                    {
                        var content = new ByteArrayContent(new byte[SyncAddress.MaxIdentityBytes + 1]);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        return new(HttpStatusCode.OK) { Content = content };
                    }
                    case Answer.Endless:
                    {
                        Endless = new EndlessStream();
                        var content = new StreamContent(Endless);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        return new(HttpStatusCode.OK) { Content = content };
                    }
                    case Answer.Trickle:
                    {
                        var content = new StreamContent(new TrickleStream());
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        return new(HttpStatusCode.OK) { Content = content };
                    }
                    case Answer.Protocol3NoCapability:
                        return Json(new { nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = 3 });
                    case Answer.OtherKey:
                        return Json(new
                        {
                            nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
                            protocolVersion = SyncProtocolVersion.Current, capabilities = MetadataCapabilities.Advertised,
                        });
                }
            }
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

    /// <summary>The handler every sync client is built on: the pin guard, over the peer.</summary>
    private HttpClient Pinned(Peer peer)
    {
        var scopeFactory = new ServiceCollection().AddSingleton(_node.WhitelistRepo).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new HttpClient(new SpkiPinGuardHandler(new SpkiPinRegistry(scopeFactory), peer));
    }

    // ---------------------------------------------------------------- F2: a redirect is "no sync server", not "no answer"

    [Fact]
    public async Task A_redirect_to_a_login_page_through_the_real_pin_guard_is_no_sync_server_and_names_neither_address()
    {
        var peer = new Peer(_peerId) { Mode = Answer.RedirectToLogin };
        using var http = Pinned(peer);

        var act = () => SyncAddress.ReadIdentityAsync(http, "http://peer.local");

        var thrown = (await act.Should().ThrowAsync<NoSyncServerException>()).Which;
        thrown.Reason.Should().Be("it answered 302 Found");
        thrown.Message.Should().NotContain(Secret).And.NotContain("login.local").And.NotContain("peer.local");
        peer.Requests.Should().Be(1, "the redirect was not followed");
    }

    [Fact]
    public async Task The_address_check_through_the_real_pin_guard_says_no_sync_server_for_a_redirect_so_no_screen_offers_to_save_it()
    {
        var peer = new Peer(_peerId) { Mode = Answer.RedirectToLogin };
        using var http = Pinned(peer);

        var check = await SyncAddress.CheckAsync(http, "http://peer.local");

        check.Verdict.Should().Be(AddressVerdict.NoSyncServer);
        check.Message.Should().StartWith("No sync server at this address:").And.NotContain(Secret).And.NotContain("login.local");
    }

    [Fact]
    public async Task The_guards_own_failure_text_carries_no_address()
    {
        var peer = new Peer(_peerId) { Mode = Answer.RedirectToLogin };
        using var http = Pinned(peer);

        var thrown = (await http.Awaiting(h => h.GetAsync("http://peer.local/api/sync/identity?x=" + Secret)).Should().ThrowAsync<HttpRequestException>()).Which;

        thrown.StatusCode.Should().Be(HttpStatusCode.Found);
        thrown.Message.Should().NotContain(Secret).And.NotContain("login.local").And.NotContain("peer.local");
    }

    [Fact]
    public async Task What_the_remote_wrote_as_the_reason_phrase_never_reaches_the_reason()
    {
        using var http = new HttpClient(new ReasonPhraseHandler("Not Found at https://internal.local/secret?token=" + Secret));

        var thrown = (await http.Awaiting(h => SyncAddress.ReadIdentityAsync(h, "http://peer.local")).Should().ThrowAsync<NoSyncServerException>()).Which;
        var check = await SyncAddress.CheckAsync(http, "http://peer.local");

        thrown.Reason.Should().Be("it answered 404 Not Found");
        thrown.Message.Should().NotContain(Secret).And.NotContain("internal.local");
        check.Message.Should().NotContain(Secret).And.NotContain("internal.local");
        PeerContactLog.Describe(thrown).Should().NotContain(Secret).And.NotContain("internal.local");
    }

    private sealed class ReasonPhraseHandler(string phrase) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = phrase });
    }

    // ---------------------------------------------------------------- 502 / 504 / captive portal: a server in trouble, still a server

    [Theory]
    [InlineData(Answer.BadGateway, HttpStatusCode.BadGateway, "it answered 502")]
    [InlineData(Answer.GatewayTimeout, HttpStatusCode.GatewayTimeout, "it answered 504")]
    [InlineData(Answer.CaptivePortal, HttpStatusCode.NetworkAuthenticationRequired, "it answered 511")]
    public async Task A_proxy_in_trouble_or_a_captive_portal_is_no_answer_not_no_sync_server(Answer answer, HttpStatusCode status, string said)
    {
        using var http = Pinned(new Peer(_peerId) { Mode = answer });

        var thrown = (await http.Awaiting(h => SyncAddress.ReadIdentityAsync(h, "http://peer.local")).Should().ThrowAsync<HttpRequestException>()).Which;
        var check = await SyncAddress.CheckAsync(http, "http://peer.local");

        thrown.StatusCode.Should().Be(status);
        check.Verdict.Should().Be(AddressVerdict.NoAnswer, "a person may be filling in the address before the server is up; a proxy that is down says nothing about what is behind it");
        check.Message.Should().Contain(said);
    }

    // ---------------------------------------------------------------- TLS

    private sealed class FailingHandler(Func<Exception> failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw failure();
    }

    [Fact]
    public async Task A_certificate_that_is_not_accepted_is_no_answer_and_says_so_without_the_address()
    {
        // what a pinned or a public-CA check does when the key or the chain is not the right one (SpkiPinRegistry.Validate returns false)
        var scopeFactory = new ServiceCollection().AddSingleton(_node.WhitelistRepo).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        using var http = new HttpClient(new SpkiPinGuardHandler(new SpkiPinRegistry(scopeFactory),
            new FailingHandler(() => new HttpRequestException("The SSL connection could not be established, see inner exception. https://peer.local/?token=" + Secret,
                new System.Security.Authentication.AuthenticationException("The remote certificate is invalid")))));

        var check = await SyncAddress.CheckAsync(http, "https://peer.local");

        check.Verdict.Should().Be(AddressVerdict.NoAnswer, "an untrusted certificate says nothing about whether a sync server is behind it");
        check.Message.Should().Contain("certificate was not accepted").And.NotContain(Secret).And.NotContain("peer.local");
    }

    // ---------------------------------------------------------------- the timeout

    [Fact]
    public async Task A_host_that_never_answers_is_no_answer_after_the_clients_timeout()
    {
        using var http = Pinned(new Peer(_peerId) { Mode = Answer.Hang });
        http.Timeout = TimeSpan.FromMilliseconds(300);

        var check = await SyncAddress.CheckAsync(http, "http://peer.local");

        check.Verdict.Should().Be(AddressVerdict.NoAnswer);
        check.Message.Should().Contain("did not answer in time");
    }

    [Fact]
    public async Task A_cancelled_check_is_cancelled_not_a_verdict()
    {
        using var http = Pinned(new Peer(_peerId) { Mode = Answer.Hang });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => SyncAddress.CheckAsync(http, "http://peer.local", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- F9: the body is bounded

    [Fact]
    public async Task A_body_declared_bigger_than_an_identity_is_no_sync_server_before_any_of_it_is_read()
    {
        using var http = Pinned(new Peer(_peerId) { Mode = Answer.Oversize });

        var act = () => SyncAddress.ReadIdentityAsync(http, "http://peer.local");

        (await act.Should().ThrowAsync<NoSyncServerException>()).Which.Reason.Should().Contain("far more than a sync identity");
    }

    [Fact]
    public async Task An_endless_body_is_cut_at_the_cap_and_is_no_sync_server()
    {
        var peer = new Peer(_peerId) { Mode = Answer.Endless };
        using var http = Pinned(peer);

        var act = () => SyncAddress.ReadIdentityAsync(http, "http://peer.local");

        (await act.Should().ThrowAsync<NoSyncServerException>()).Which.Reason.Should().Contain("far more than a sync identity");
        peer.Endless!.Served.Should().BeLessThanOrEqualTo(SyncAddress.MaxIdentityBytes + 8192, "no more than the cap and one buffer was ever read");
    }

    [Fact]
    public async Task A_body_that_trickles_is_no_answer_within_the_body_timeout_and_not_a_hung_screen()
    {
        using var http = Pinned(new Peer(_peerId) { Mode = Answer.Trickle });
        var started = DateTime.UtcNow;

        var check = await SyncAddress.CheckAsync(http, "http://peer.local", bodyTimeout: TimeSpan.FromMilliseconds(250));

        check.Verdict.Should().Be(AddressVerdict.NoAnswer);
        check.Message.Should().Contain("did not answer in time");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // ---------------------------------------------------------------- F4 at the sync client: the key, not only the id

    private async Task AddPeerRowAsync(byte[]? key = null) =>
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Laptop", Ed25519PublicKey = key ?? new byte[32], ApiAddress = "http://peer.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    [Fact]
    public async Task A_peer_with_the_right_id_and_another_key_is_refused_by_the_sync_client()
    {
        await AddPeerRowAsync();
        using var http = new HttpClient(new Peer(_peerId) { Mode = Answer.OtherKey });

        var act = () => _client.SyncWithAsync(http, "http://peer.local", _peerId);

        var thrown = (await act.Should().ThrowAsync<PeerKeyMismatchException>()).Which;
        thrown.Message.Should().NotContain("peer.local");
        PeerContactLog.Describe(thrown).Should().Contain("another key");
    }

    [Fact]
    public async Task A_peer_with_the_key_on_record_is_synced_with()
    {
        await AddPeerRowAsync();
        using var http = new HttpClient(new Peer(_peerId));

        var act = () => _client.SyncWithAsync(http, "http://peer.local", _peerId);

        await act.Should().NotThrowAsync();
    }

    // ---------------------------------------------------------------- F1: a peer that is too old is a failure, at every layer

    private async Task<(SyncScheduler Scheduler, Peer Peer, PeerContactLog Log)> NodeAsync(Answer answer)
    {
        await AddPeerRowAsync();
        var peer = new Peer(_peerId) { Mode = answer };
        var log = new PeerContactLog();
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client).AddSingleton(log)
            .BuildServiceProvider();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncScheduler>.Instance, new SyncTrigger(), new Factory(peer));
        return (scheduler, peer, log);
    }

    [Fact]
    public async Task After_the_activation_a_protocol_3_peer_without_the_envelope_capability_fails_the_session()
    {
        await AddPeerRowAsync();
        _policy.MarkActivated();
        using var http = new HttpClient(new Peer(_peerId) { Mode = Answer.Protocol3NoCapability });

        var act = () => _client.SyncWithAsync(http, "http://peer.local", _peerId);

        var thrown = (await act.Should().ThrowAsync<PeerIncompatibleException>()).Which;
        thrown.RemoteProtocol.Should().Be(3);
        thrown.Message.Should().NotContain("peer.local");
    }

    [Fact]
    public async Task Before_the_activation_the_same_peer_is_synced_with()
    {
        await AddPeerRowAsync();
        using var http = new HttpClient(new Peer(_peerId) { Mode = Answer.Protocol3NoCapability });

        var act = () => _client.SyncWithAsync(http, "http://peer.local", _peerId);

        await act.Should().NotThrowAsync("a 2.6 peer that has not applied the activation yet still syncs");
    }

    [Fact]
    public async Task The_incompatible_peer_reaches_the_cycle_result_the_contact_log_and_the_check_and_never_says_ok()
    {
        var (scheduler, peer, log) = await NodeAsync(Answer.Protocol3NoCapability);
        scheduler.CheckMinGap = TimeSpan.Zero;
        _policy.MarkActivated();

        var result = await scheduler.SyncAllAsync(CancellationToken.None);

        // what the phone's foreground service and WorkManager heartbeat read: Problem is null only for a cycle that served every peer
        result.TotalApplied.Should().Be(0);
        result.Problem.Should().Contain("older version (protocol 3)").And.Contain("upgrade it");
        result.PeerFailures.Should().ContainSingle().Which.NodeId.Should().Be(_peerId);
        var contact = log.Get(_peerId, "http://peer.local");
        contact.OutboundFailure.Should().Contain("older version (protocol 3)");
        contact.OutboundOkAt.Should().BeNull("it was not a session that went well");

        // the cycles that follow leave it alone (a growing pause, one warning) and still say it is not served
        var asked = peer.Requests;
        var next = await scheduler.SyncAllAsync(CancellationToken.None);
        peer.Requests.Should().Be(asked);
        next.Problem.Should().Contain("older version (protocol 3)");

        // a person who checks it now is told what is wrong, and that it answered
        var check = await scheduler.CheckPeerNowAsync(_peerId);
        check.Called.Should().BeTrue();
        check.Answered.Should().BeTrue();
        check.Reason.Should().Contain("older version (protocol 3)");

        // when it is upgraded the next session is clean and the failure is gone
        peer.Mode = Answer.Good;
        (await scheduler.CheckPeerNowAsync(_peerId)).Reason.Should().BeNull();
        (await scheduler.SyncAllAsync(CancellationToken.None)).Problem.Should().BeNull();
    }
}
