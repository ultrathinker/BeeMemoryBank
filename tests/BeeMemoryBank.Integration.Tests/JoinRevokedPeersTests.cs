using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A join hands the joiner the host's revocations (2.8.0): <c>whitelist</c> stays the active peers only, and the revoked ones travel in their own
/// optional <c>revokedPeers</c>, with the version of each revoke. The joiner keeps them as revoked rows, so an old <c>whitelist_add</c> for such a peer
/// that reaches it later from another node's log is refused, as on the host. Both directions with 2.7.5 stay as they were: a 2.7.5 joiner reads the
/// answer exactly as before (it never sees the revoked peer, and could only have made an active row of it from <c>whitelist</c>), and a joiner of
/// this version joins a 2.7.5 host, which sends no <c>revokedPeers</c>, as before.
/// </summary>
public class JoinRevokedPeersTests : IAsyncLifetime
{
    private const string Password = "joinRevokedPeersPassword1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Guid _revokedPhone = Guid.NewGuid();
    private readonly Guid _activePeer = Guid.NewGuid();
    private readonly byte[] _phoneKey = Ed25519Signer.GenerateKeyPair().publicKey;

    private BmbWebApplicationFactory _host = null!;
    private HttpClient _hostClient = null!;
    private SyncEvent _staleAdd = null!;
    private RowVersion _revoke;
    private RowVersion _activeVersion;

    public async Task InitializeAsync()
    {
        // The limiter of /api/join is process-wide; these joins must neither hit it nor use up what other classes count on.
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        _host = new BmbWebApplicationFactory();
        _hostClient = _host.CreateClient();
        await _host.InitializeNodeAsync("RevokingHost", Password);
        (await _hostClient.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
        // A new node joins only a node on protocol 4 (BMB-306).
        await StepANode.ActivateAsync(_host);

        using var scope = _host.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var events = scope.ServiceProvider.GetRequiredService<IEventLogger>();
        var now = DateTime.UtcNow;

        // The phone is added, then revoked: the add stays in the host's log, as it stays in the log of every node that had it.
        var phone = new WhitelistEntry
        {
            NodeId = _revokedPhone, DisplayName = "Test Phone", Ed25519PublicKey = _phoneKey, Status = WhitelistStatuses.Active, CreatedAt = now, UpdatedAt = now
        };
        var added = await events.LogWhitelistAddAsync(phone);
        phone.LamportTs = added.LamportTs;
        phone.SourceNodeId = added.SourceNodeId;
        await whitelist.CreateAsync(phone);
        _revoke = await events.LogWhitelistRevokeAsync(_revokedPhone);
        await whitelist.RevokeAsync(_revokedPhone, _revoke);
        _staleAdd = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 100_000))
            .Single(e => e.EventType == EventTypes.WhitelistAdd && e.Payload.Contains(_revokedPhone.ToString()));

        var laptop = new WhitelistEntry
        {
            NodeId = _activePeer, DisplayName = "Laptop", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            ApiAddress = "https://laptop.example:5301", Status = WhitelistStatuses.Active, CreatedAt = now, UpdatedAt = now
        };
        _activeVersion = await events.LogWhitelistAddAsync(laptop);
        laptop.LamportTs = _activeVersion.LamportTs;
        laptop.SourceNodeId = _activeVersion.SourceNodeId;
        await whitelist.CreateAsync(laptop);
    }

    public Task DisposeAsync()
    {
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        _hostClient.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheJoinAnswer_CarriesTheRevokedPeerApartFromTheWhitelist_WithItsRevokesVersion()
    {
        var answer = await JoinAnswerAsync();

        answer.GetProperty("whitelist").EnumerateArray().Select(e => e.GetProperty("nodeId").GetGuid())
            .Should().Contain(_activePeer).And.NotContain(_revokedPhone, "a joiner of any version makes an active row of every entry there");
        var revoked = answer.GetProperty("revokedPeers").EnumerateArray().Single(e => e.GetProperty("nodeId").GetGuid() == _revokedPhone);
        revoked.GetProperty("lamportTs").GetInt64().Should().Be(_revoke.LamportTs);
        revoked.GetProperty("sourceNodeId").GetGuid().Should().Be(_revoke.SourceNodeId);
        revoked.GetProperty("ed25519PublicKeyB64").GetString().Should().Be(Convert.ToBase64String(_phoneKey));

        var active = answer.GetProperty("whitelist").EnumerateArray().Single(e => e.GetProperty("nodeId").GetGuid() == _activePeer);
        (active.GetProperty("lamportTs").GetInt64(), active.GetProperty("sourceNodeId").GetGuid())
            .Should().Be((_activeVersion.LamportTs, _activeVersion.SourceNodeId), "an active peer carries the version of the host's row");
    }

    /// <summary>
    /// A 2.7.5 joiner (its answer type and serializer options, copied from that release) reads this version's answer as it always did: the
    /// member it does not know is ignored, and the revoked peer is nowhere it would make a row of.
    /// </summary>
    [Fact]
    public async Task A275Joiner_ReadsTheAnswerAsBefore_AndNeverSeesTheRevokedPeerAsAPeer()
    {
        var raw = (await JoinAnswerAsync()).GetRawText();

        var old = JsonSerializer.Deserialize<Release275JoinResponse>(raw, Release275JsonOptions);

        old.Should().NotBeNull();
        old!.RemoteNode.NodeId.Should().NotBeEmpty();
        old.KeySlot.EncryptedMasterDekB64.Should().NotBeNullOrEmpty();
        old.Whitelist!.Select(e => e.NodeId).Should().Contain(_activePeer).And.NotContain(_revokedPhone);
    }

    [Fact]
    public async Task TheSetupPagesJoin_KeepsTheRevokedPeerRevoked_AndRefusesItsOldAdd()
    {
        using var joiner = await JoinThroughSetupAsync(_host.Server.CreateHandler());

        using var scope = joiner.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var tombstone = await rows.GetByNodeIdAsync(_revokedPhone, includeDeleted: true);
        (tombstone!.Status, tombstone.Version).Should().Be((WhitelistStatuses.Revoked, _revoke));
        tombstone.Ed25519PublicKey.Should().Equal(_phoneKey);
        (await rows.GetAllActiveAsync()).Select(e => e.NodeId).Should().Contain(_activePeer).And.NotContain(_revokedPhone);
        (await rows.GetByNodeIdAsync(_activePeer))!.Version.Should().Be(_activeVersion, "the joiner's row takes the host's version");
        scope.ServiceProvider.GetRequiredService<ILamportClock>().Current.Should().BeGreaterThanOrEqualTo(_revoke.LamportTs,
            "a re-add made on the joiner must outrank the revoke it took over");

        // The add, older than the revoke, arrives from some other node's log.
        (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(_staleAdd)).Should().Be(EventApplyResult.Applied);

        var after = await rows.GetByNodeIdAsync(_revokedPhone, includeDeleted: true);
        (after!.Status, after.Version).Should().Be((WhitelistStatuses.Revoked, _revoke), "the joiner refuses the add the host refuses");
    }

    /// <summary>
    /// A host of 2.7.5 sends no <c>revokedPeers</c> and no versions on its peers (simulated by taking them out of this host's answer): the join of
    /// this version goes through as before, with the active peers at version 0 and nothing else.
    /// </summary>
    [Fact]
    public async Task AJoinerOfThisVersion_JoinsA275Host_AsBefore()
    {
        using var joiner = await JoinThroughSetupAsync(new WithoutRevokedPeers(_host.Server.CreateHandler()));

        using var scope = joiner.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await rows.GetByNodeIdAsync(_activePeer))!.LamportTs.Should().Be(0, "a 2.7.5 host sends no version: the row loses to every event, as before");
        (await rows.GetByNodeIdAsync(_revokedPhone, includeDeleted: true)).Should().BeNull("that host said nothing about it");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<JsonElement> JoinAnswerAsync()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var resp = await _hostClient.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId = Guid.NewGuid(),
            displayName = "Joiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null,
            protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol   // an activated host takes only a 2.6 joiner
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private static async Task<BmbWebApplicationFactory> JoinThroughSetupAsync(HttpMessageHandler toHost)
    {
        var joiner = new BmbWebApplicationFactory();
        joiner.RouteOutboundHttpThrough(toHost);
        var resp = await joiner.CreateClient().PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            passwordHint = "A test hint",
            displayName = "Joiner",
            remoteUrl = "http://host", // arbitrary: RouteOutboundHttpThrough ignores the host
            password = Password
        }, JsonOpts);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        (await joiner.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue("the joined node is encrypted and locked (BMB-306)");
        return joiner;
    }

    /// <summary>The answer of <c>POST /api/join</c> without <c>revokedPeers</c> and without the versions of its peers: what a host of 2.7.5 sends.</summary>
    private sealed class WithoutRevokedPeers(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/api/join" || !response.IsSuccessStatusCode) return response;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            body.Remove("revokedPeers").Should().BeTrue("this host's answer carries the member the test takes out");
            foreach (var entry in body["whitelist"]!.AsArray())
            {
                entry!.AsObject().Remove("lamportTs");
                entry.AsObject().Remove("sourceNodeId");
                entry.AsObject().Remove("versions");
            }
            response.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            return response;
        }
    }

    // The answer type and serializer options of the joiners of release 2.7.5 (InitEndpoints, JoinCommand and NodeSetupService had the same).
    private static readonly JsonSerializerOptions Release275JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Release275JoinResponse(Release275RemoteNode RemoteNode, Release275KeySlot KeySlot, List<Release275WhitelistEntry>? Whitelist);

    private sealed record Release275RemoteNode(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion);

    private sealed record Release275WhitelistEntry(
        Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, string? ApiAddress, bool IsSuperadmin = false, string? TlsTrust = null, string? TlsSpki = null);

    private sealed record Release275KeySlot(
        string EncryptedMasterDekB64, string IvB64, string SaltB64, int ArgonMemory, int ArgonIterations, int ArgonParallelism);
}
