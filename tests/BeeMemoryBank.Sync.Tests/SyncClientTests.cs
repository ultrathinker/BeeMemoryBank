using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

public class SyncClientTests : IAsyncLifetime
{
    private SyncTestFixture _node = null!;
    private PeerNewerProtocolState _peerNewerProtocolState = null!;
    private SyncClient _client = null!;
    private MockHandler _mockHandler = null!;
    private HttpClient _http = null!;
    private Guid _remoteNodeId;

    public async Task InitializeAsync()
    {
        _node = new ConcreteFixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");

        // Create an article to ensure there is at least one local event to push.
        await _node.ArticleService.CreateAsync("Test Article", "/Root", new List<string>(), "content");

        _peerNewerProtocolState = new PeerNewerProtocolState();

        var syncPositionRepo = new SyncPositionRepository(_node.Factory);
        var pushPositionRepo = new SyncPushPositionRepository(_node.Factory);
        var authSigner = new SessionNodeAuthSigner(_node.Session);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance;

        _client = new SyncClient(
            _node.NodeRepo,
            _node.EventLogRepo,
            syncPositionRepo,
            pushPositionRepo,
            _node.EventApplier,
            _node.Session,
            authSigner,
            logger,
            _peerNewerProtocolState,
            _node.QuarantineRepo,
            new BlobRepository(_node.Factory));

        _remoteNodeId = Guid.NewGuid();
        _mockHandler = new MockHandler();
        _http = new HttpClient(_mockHandler) { BaseAddress = new Uri("http://remote.local") };

        // Default mock routes
        _mockHandler.MapRoute("/api/sync/sentinel", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        _mockHandler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32]),
                serverNodeId = _remoteNodeId
            }), Encoding.UTF8, "application/json")
        });
        _mockHandler.MapRoute("/api/sync/authenticate", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                token = "test-token"
            }), Encoding.UTF8, "application/json")
        });
        _mockHandler.MapRoute("/api/sync/events", req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }
            else // POST (push)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        applied = 1,
                        skipped = 0,
                        lastAppliedSequence = 1,
                        dropped = 0
                    }), Encoding.UTF8, "application/json")
                };
            }
        });
        _mockHandler.MapRoute("/api/sync/report-position", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        // Blob transport (protocol 2): the peer reports every hash as missing, so the article's
        // blob is uploaded ahead of its event — the wire sequence asserted by the push tests.
        _mockHandler.MapRoute("/api/sync/blobs/check", req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var hashes = JsonDocument.Parse(body).RootElement.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()!).ToList();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { missing = hashes }), Encoding.UTF8, "application/json")
            };
        });
        _mockHandler.MapRoute("/api/sync/blobs", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { stored = 1, rejected = 0 }), Encoding.UTF8, "application/json")
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _node.DisposeAsync();
    }

    [Fact]
    public async Task SyncWith_PeerEqualVersion_SyncsNormally()
    {
        // Arrange
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteEqual",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        // Act
        var result = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        // Assert
        result.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeFalse();
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("GET") && s.Contains("/api/sync/events"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/report-position"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/events")); // push

        // Protocol 2 wire order: the peer is asked which blobs it lacks, they are uploaded, and
        // only then do the events go — the receiver cannot fetch bytes it is missing on its own.
        var log = _mockHandler.CallLog;
        var check  = log.FindIndex(s => s == "POST /api/sync/blobs/check");
        var upload = log.FindIndex(s => s == "POST /api/sync/blobs");
        var push   = log.FindIndex(s => s == "POST /api/sync/events");
        check.Should().BeGreaterThanOrEqualTo(0);
        upload.Should().BeGreaterThan(check);
        push.Should().BeGreaterThan(upload);
    }

    /// <summary>
    /// Review L-stage0 #2: a peer below protocol 3 would accept a blind node's events, so nothing
    /// moves either way — not even a pull, whose events could have come from a blind node.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task SyncWith_PeerBelowProtocol3_NeitherPullsNorPushes(int remoteVersion)
    {
        // Arrange
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteLower",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = remoteVersion
            }), Encoding.UTF8, "application/json")
        });

        // Act
        var result = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        // Assert
        result.Should().Be(0);
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/challenge") || s.Contains("/api/sync/authenticate")
            || s.Contains("/api/sync/events") || s.Contains("/api/sync/blobs") || s.Contains("/api/sync/report-position"),
            "no token is requested and no event goes either way");
    }

    [Fact]
    public async Task SyncWith_PeerHigherVersion_SkipsPull_PushesNormally()
    {
        // Arrange
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteHigher",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current + 1
            }), Encoding.UTF8, "application/json")
        });

        // Act
        var result = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        // Assert
        result.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeTrue();
        _mockHandler.CallLog.Should().NotContain(s => s.StartsWith("GET") && s.Contains("/api/sync/events"));
        // Our position is still reported: the peer's compaction gate reads it, and a node that
        // stopped reporting because it cannot apply newer events would freeze the peer's log.
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/report-position"));
        _mockHandler.CallLog.Should().Contain(s => s.StartsWith("POST") && s.Contains("/api/sync/events")); // push still happens
    }

    /// <summary>
    /// A peer answering /api/sync/blobs/get with hashes we never asked for (or the same one over
    /// and over) must not keep BlobTransport asking forever — the loop gives up on that group
    /// after a round with no progress, and the sync cycle finishes.
    /// </summary>
    [Fact]
    public async Task SyncWith_PeerReturnsUnrequestedBlobs_FetchLoopTerminates()
    {
        var referenced = new string('a', 64);
        var unrelated = new string('b', 64);
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId, displayName = "Remote",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });
        // One pulled event naming a blob we do not have. Its signature will not verify (the remote
        // is not whitelisted here) — irrelevant: the blob fetch happens before any apply.
        _mockHandler.MapRoute("/api/sync/events", req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        eventId = Guid.NewGuid(), nodeId = _remoteNodeId, lamportTs = 1, sequenceNum = 1,
                        eventType = EventTypes.ArticleCreate, articleId = Guid.NewGuid(),
                        payload = $$"""{"title":"x","ciphertext":null,"ciphertext_sha256":"{{referenced}}"}""",
                        signature = Convert.ToBase64String(new byte[64]), protocolVersion = 2, createdAt = DateTime.UtcNow
                    }
                }), Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 }), Encoding.UTF8, "application/json")
            });
        int getCalls = 0;
        _mockHandler.MapRoute("/api/sync/blobs/get", _ =>
        {
            getCalls++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    blobs = new[] { new { hash = unrelated, data = Convert.ToBase64String(new byte[] { 1 }) } }
                }), Encoding.UTF8, "application/json")
            };
        });

        var run = _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        finished.Should().BeSameAs(run, "the blob fetch loop must terminate when the peer makes no progress");
        await run;
        getCalls.Should().Be(1);
    }

    [Fact]
    public async Task SyncWith_PeerHigherVersion_ThenPeerEqualVersion_ClearsFlag()
    {
        _peerNewerProtocolState.HasNewerProtocol = false;
        
        bool isHigher = true;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "RemoteNode",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = isHigher ? SyncProtocolVersion.Current + 1 : SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        var result1 = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);
        result1.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeTrue();

        isHigher = false;

        var result2 = await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);
        result2.Should().Be(0);
        _peerNewerProtocolState.HasNewerProtocol.Should().BeFalse();
    }

    // ─── M6: caller-pinned audience anchor ─────────────────────────────────────

    [Fact]
    public async Task SyncWith_PeerDeclaresADifferentNodeIdThanWeDialed_RefusesToSync()
    {
        // We dial _remoteNodeId — in production, the whitelist entry SyncScheduler is iterating.
        // The peer's own /api/sync/identity response claims to be someone else: a stale/incorrect
        // ApiAddress entry, or a peer impersonating the node we meant to reach. SyncClient's
        // fast-fail check must refuse before ever touching the network for a challenge.
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = Guid.NewGuid(), // self-declared — deliberately != the pinned _remoteNodeId
                displayName = "Impersonator",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        var act = async () => await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*but we dialed it as*");

        // Refused before ever fetching a challenge.
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/challenge"));
    }

    [Fact]
    public async Task SyncWith_ChallengeClaimsForeignAudience_RefusesToSign_EvenWhenSelfDeclaredIdentityMatches()
    {
        // The actually security-relevant case (the M6 hole this fix closes): the peer's
        // self-declared /api/sync/identity AGREES with the id we dialed — so the fast-fail check
        // above does NOT catch it — but /api/sync/challenge claims a DIFFERENT ServerNodeId,
        // modeling a peer that relays a genuine challenge fetched live from some unrelated third
        // node. PeerAuthenticator must still refuse to sign, because the audience anchor is the
        // NodeId the CALLER dialed, never whatever this same connection claims about itself.
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId, // matches the pin — the shallow check alone would pass
                displayName = "SelfConsistentRelay",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });

        var foreignNodeId = Guid.NewGuid();
        _mockHandler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32]),
                serverNodeId = foreignNodeId // relayed from an unrelated third node
            }), Encoding.UTF8, "application/json")
        });

        var act = async () => await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Challenge audience mismatch*");

        // Refused right after the challenge — never got as far as posting a signature.
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/authenticate"));
    }

    [Fact]
    public async Task SyncWith_PeerChallengeOmitsServerNodeId_RefusesToSign_NoUnboundFallback()
    {
        // A challenge response with no ServerNodeId used to be read as "an old peer that predates
        // audience binding" and answered with an unbound V1 signature. That was a downgrade
        // anyone could trigger: the responding peer decides whether to send the field, so an
        // attacker only had to omit it, hand us a challenge fetched live from node C, and redeem
        // the resulting unbound signature at C — the exact relay attack the binding exists to
        // stop, reachable by deleting one JSON property. There is no fallback any more: a peer
        // that declares no audience gets no signature.
        _peerNewerProtocolState.HasNewerProtocol = false;
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "OldPeer",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion = SyncProtocolVersion.Current
            }), Encoding.UTF8, "application/json")
        });
        _mockHandler.MapRoute("/api/sync/challenge", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            // No serverNodeId field at all (not even null — absent).
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                challenge = Convert.ToBase64String(new byte[32])
            }), Encoding.UTF8, "application/json")
        });

        var act = async () => await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no ServerNodeId*");

        // Never posted a signature of any kind.
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/authenticate"));
    }

    /// <summary>
    /// Plan 3.1: a peer on protocol 2 does not know the blind-node mark — it would accept a blind
    /// node's events and seal the DEK for one. It must not receive a single event from us.
    /// </summary>
    [Fact]
    public async Task SyncWith_PeerOnProtocol2_GetsNoPush_ItPredatesBlindNodes()
    {
        MapIdentity(protocolVersion: 2);

        await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        _mockHandler.CallLog.Should().NotContain(s => s.StartsWith("POST") && s.EndsWith("/api/sync/events"));
        _mockHandler.CallLog.Should().NotContain(s => s.Contains("/api/sync/blobs"));
    }

    /// <summary>
    /// The peer can only keep "last seen protocol" for us (the PC's pre-flight before adding a
    /// blind node reads it) if we declare it, in both places the peer records it.
    /// </summary>
    [Fact]
    public async Task SyncWith_DeclaresOwnProtocol_InAuthenticateAndReportPosition()
    {
        MapIdentity(protocolVersion: SyncProtocolVersion.Current);
        int? declaredAtAuth = null;
        string? reportQuery = null;
        _mockHandler.MapRoute("/api/sync/authenticate", req =>
        {
            var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            declaredAtAuth = body.RootElement.TryGetProperty("protocolVersion", out var declared)
                ? declared.GetInt32()
                : null;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = "test-token" }), Encoding.UTF8, "application/json")
            };
        });
        _mockHandler.MapRoute("/api/sync/report-position", req =>
        {
            reportQuery = req.RequestUri!.Query;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        declaredAtAuth.Should().Be(SyncProtocolVersion.Current);
        reportQuery.Should().Contain($"protocolVersion={SyncProtocolVersion.Current}");
    }

    /// <summary>
    /// F5. A blind node that was reseeded replays the tail it had received onto the package and
    /// re-logs it under the new database's sequences, so a pull brings back events this node authored.
    /// While such an event is still in our own log the applier's "already applied" shortcut drops it;
    /// after a compaction it is not, so it reaches the whitelist lookup — where we are not a peer of
    /// ourselves — and is recorded as a failure. That row is not cosmetic: the state anchor publishes
    /// nothing while tbl_sync_quarantine holds one, so anchors stop for good.
    /// </summary>
    [Fact]
    public async Task SyncWith_OurOwnEventComingBackFromAPeer_IsSkipped_NotRecordedAsAFailure()
    {
        MapIdentity(SyncProtocolVersion.Current);
        var self = (await _node.NodeRepo.GetAsync())!;
        _mockHandler.MapRoute("/api/sync/events", req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new[] { PulledEvent(self.NodeId, sequenceNum: 9) }), Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 }), Encoding.UTF8, "application/json")
            });
        var positions = new SyncPositionRepository(_node.Factory);

        await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await _node.QuarantineRepo.GetAllAsync()).Should().BeEmpty(
            "an event of our own is applied here already, or was compacted into the state — never a reason to stop anchoring");
        (await positions.GetAsync(_remoteNodeId))!.LastSequenceNum.Should().Be(9, "the cursor still moves past it");
    }

    /// <summary>
    /// F6. A quiet network: the peer answers every pull with nothing new, so the sequence number never
    /// moves and its position row is never touched again. The state anchor judges "caught up with this
    /// peer" by that row's timestamp (<c>StateAnchorScheduler.FreshPull</c>), so it ages out and no
    /// anchor is ever published again — the pull itself is the evidence, not the events.
    /// </summary>
    [Fact]
    public async Task SyncWith_NothingToPull_StillRefreshesThePosition()
    {
        MapIdentity(SyncProtocolVersion.Current);
        var positions = new SyncPositionRepository(_node.Factory);
        await positions.UpsertAsync(new SyncPosition
        {
            RemoteNodeId = _remoteNodeId,
            LastSequenceNum = 7,
            UpdatedAt = DateTime.UtcNow - TimeSpan.FromHours(3)
        });

        var before = DateTime.UtcNow;
        await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        var after = await positions.GetAsync(_remoteNodeId);
        after!.LastSequenceNum.Should().Be(7, "an empty pull brings nothing new");
        after.UpdatedAt.Should().BeAfter(before, "we did pull from this peer, and that is what the anchor reads");
    }

    /// <summary>
    /// Codex round 2, security #2. The node id rides on the wire and is not covered by the signature,
    /// so an event that merely <i>claims</i> to come from this node proves nothing — and the F5 skip
    /// used to take that claim at face value and advance the cursor over it. A peer that fabricates
    /// one with a sequence past the end of what we hold makes us walk past every real event behind it,
    /// and on a blind node past the blind-authorship invariant as well.
    ///
    /// <para>An unproven claim now goes through the ordinary apply: the cursor stops at it and the
    /// event is refused the way any unverifiable originator is.</para>
    /// </summary>
    [Fact]
    public async Task SyncWith_ForgedEventClaimingOurOwnNodeId_IsNotSkipped_AndTheCursorDoesNotMove()
    {
        MapIdentity(SyncProtocolVersion.Current);
        var self = (await _node.NodeRepo.GetAsync())!;
        var page = new[] { PulledEvent(self.NodeId, sequenceNum: 42) };
        _mockHandler.MapRoute("/api/sync/events", req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page), Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 }), Encoding.UTF8, "application/json")
            });
        var positions = new SyncPositionRepository(_node.Factory);
        await positions.UpsertAsync(new SyncPosition
        {
            RemoteNodeId = _remoteNodeId, LastSequenceNum = 5, UpdatedAt = DateTime.UtcNow
        });

        await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await positions.GetAsync(_remoteNodeId))!.LastSequenceNum.Should().Be(5,
            "an event that names us but cannot be proven ours must never move the cursor");
        (await _node.QuarantineRepo.GetAllAsync()).Should().NotBeEmpty(
            "it is refused exactly as any other event we cannot verify");
    }

    /// <summary>
    /// The other half of the same rule, and the reason the skip exists at all (F5): an event of ours
    /// that IS proven — here by the signature, with the row a compaction would have taken away already
    /// gone — is still skipped, and never quarantined.
    /// </summary>
    [Fact]
    public async Task SyncWith_OurOwnEventProvenByItsSignature_IsSkipped_EvenWhenOurLogNoLongerHasIt()
    {
        MapIdentity(SyncProtocolVersion.Current);
        var self = (await _node.NodeRepo.GetAsync())!;
        await _node.ArticleService.CreateAsync("Mine", "/Root", [], "content");
        var mine = (await _node.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        mine.NodeId.Should().Be(self.NodeId);
        mine.EventType.Should().Be(BeeMemoryBank.Sync.EventTypes.ArticleCreate);

        // What a compaction leaves behind: the event is ours and signed by us, but our own log no
        // longer holds the row, so the applier's "already applied" shortcut cannot see it.
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync("DELETE FROM tbl_event WHERE event_id = @id", new { id = mine.EventId });
        (await _node.EventLogRepo.ExistsAsync(mine.EventId)).Should().BeFalse();

        mine.SequenceNum = 9;
        var page = new[] { mine };
        _mockHandler.MapRoute("/api/sync/events", req => req.Method == HttpMethod.Get
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 }), Encoding.UTF8, "application/json")
            });
        var positions = new SyncPositionRepository(_node.Factory);
        await positions.UpsertAsync(new SyncPosition
        {
            RemoteNodeId = _remoteNodeId, LastSequenceNum = 4, UpdatedAt = DateTime.UtcNow
        });

        await _client.SyncWithAsync(_http, "http://remote.local", _remoteNodeId);

        (await positions.GetAsync(_remoteNodeId))!.LastSequenceNum.Should().Be(9,
            "a proven own event is ours: there is nothing to apply, and the cursor moves past it");
        (await _node.QuarantineRepo.GetAllAsync()).Should().BeEmpty(
            "and nothing to defer — a quarantine row would stop the state anchor for good");
    }

    /// <summary>An event as it arrives in a pull page.</summary>
    private static object PulledEvent(Guid origin, long sequenceNum) => new
    {
        eventId = Guid.NewGuid(),
        nodeId = origin,
        lamportTs = 1,
        sequenceNum,
        eventType = EventTypes.FolderCreate,
        payload = "{}",
        signature = Convert.ToBase64String(new byte[64]),
        protocolVersion = 2,
        createdAt = DateTime.UtcNow
    };

    private void MapIdentity(int protocolVersion) =>
        _mockHandler.MapRoute("/api/sync/identity", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                nodeId = _remoteNodeId,
                displayName = "Remote",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                protocolVersion
            }), Encoding.UTF8, "application/json")
        });

    private class ConcreteFixture : SyncTestFixture { }

    private class MockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<string> CallLog { get; } = new();

        public void MapRoute(string path, Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _routes[path] = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("No URI");
            CallLog.Add($"{request.Method} {uri.AbsolutePath}");

            foreach (var (path, handler) in _routes)
            {
                if (uri.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(handler(request));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
