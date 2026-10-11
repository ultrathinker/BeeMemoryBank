using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SealedEnvelope = BeeMemoryBank.Sync.Metadata.SealedEnvelope;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The relay and the import against a whole node (docs/design/step-b-spec.md 6.6, red tests 15.2.9 and 15.2.10): the node's vault is the real
/// one with the real <see cref="EventApplier"/>; a gate that says Closed hands the relay set to <see cref="ISealedRelay"/> (what the sealed-vault
/// middleware of track 3 will do), peers push, the node "unlocks" and imports. Every event is applied exactly once in the order it was received,
/// a revoke received while closed takes effect at its place, a node revoked before the close is refused, numbering continues above every relay
/// sequence served, reports are merged and the inbox is empty.
/// </summary>
public class SealedRelayFullNodeTests : IAsyncLifetime
{
    private const string Password = "testPassword123";

    private sealed class RelayFirstFilter(FakeGate gate) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                var relay = context.RequestServices.GetRequiredService<ISealedRelay>();
                if (gate.State is VaultState.Closed or VaultState.Opening or VaultState.Importing && relay.Handles(context.Request.Path, context.Request.Method))
                    await relay.HandleAsync(context);
                else
                    await following();
            });
            next(app);
        };
    }

    private readonly BmbWebApplicationFactory _baseFactory = new();
    // Not sealed while the host starts and the node is set up (the database of this host is a plain test file); sealed when it "closes" at the end of set-up.
    private readonly FakeGate _gate = new(isSealed: false);
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private RelayPeer _admin = null!;
    private RelayPeer _member = null!;
    private RelayPeer _revokedBefore = null!;

    private T Resolve<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var keys = new FakeVaultKeys(RandomNumberGenerator.GetBytes(32));
        _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddBootStore(_baseFactory.DataPath);
            s.AddSingleton<IVaultGate>(_gate);
            s.AddSingleton<ISealedVaultKeys>(keys);
            s.AddSealedUnlockMaterial();
            s.AddSealedRelay();
            s.AddSingleton<IStartupFilter>(new RelayFirstFilter(_gate));
        }));
        _client = _factory.CreateClient();

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "TestNode", Password);

        _admin = await AddPeerAsync(superadmin: true, status: "A");
        _member = await AddPeerAsync(superadmin: false, status: "A");
        _revokedBefore = await AddPeerAsync(superadmin: false, status: "R");

        // The close: the mirror copies the identity and the whitelist, the lifecycle records the head, and the gate says Closed.
        var boot = Resolve<BootStore>();
        await boot.EnsureCreatedAsync();
        var self = (await Resolve<INodeIdentityRepository>().GetAsync())!;
        await boot.SetMetaAsync(BootMetaKeys.NodeId, self.NodeId.ToString("D"));
        await boot.SetMetaAsync(BootMetaKeys.DbkGeneration, "1");
        await boot.SetMetaAsync(BootMetaKeys.SeqFloor, "1");
        _gate.IsSealed = true;   // the mirror writes only for a sealed node
        await Resolve<UnlockMaterialMirror>().RewriteAsync();
        await Resolve<ISealedRelayLifecycle>().OnClosingAsync();
        _gate.State = VaultState.Closed;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        ((IDisposable)_baseFactory).Dispose();
        return Task.CompletedTask;
    }

    private async Task<RelayPeer> AddPeerAsync(bool superadmin, string status)
    {
        var peer = RelayPeer.New();
        await Resolve<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peer.Id, DisplayName = "Peer " + peer.Id.ToString("N")[..6], Ed25519PublicKey = peer.PublicKey, Status = status,
            IsSuperadmin = superadmin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return peer;
    }

    private async Task<string> TokenAsync(RelayPeer peer)
    {
        var challengeResponse = await _client.PostAsync("/api/sync/challenge", null);
        challengeResponse.StatusCode.Should().Be(HttpStatusCode.OK, await challengeResponse.Content.ReadAsStringAsync());
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<SyncChallengeResponse>())!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(challenge.ServerNodeId.ToByteArray()).Concat(Convert.FromBase64String(challenge.Challenge)).ToArray();
        var response = await _client.PostAsJsonAsync("/api/sync/authenticate", new SyncAuthRequest(peer.Id, challenge.Challenge,
            Convert.ToBase64String(Ed25519Signer.Sign(peer.PrivateKey, payload)), EventClasses.SealedProtocol, MetadataCapabilities.Advertised, "2.6.0"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SyncAuthResponse>())!.Token;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private async Task<SyncApplyResult> Push(string token, params SyncEvent[] events)
    {
        var response = await Send(HttpMethod.Post, "/api/sync/events", token, events);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SyncApplyResult>())!;
    }

    private async Task<List<(long Sequence, Guid EventId)>> LogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        using var c = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await c.QueryAsync<(long, string)>("SELECT sequence_num, event_id FROM tbl_event ORDER BY sequence_num"))
            .Select(r => (r.Item1, Guid.Parse(r.Item2))).ToList();
    }

    [Fact]
    public async Task WhatAClosedNodeReceived_IsAppliedExactlyOnceInOrderAtUnlock_AndTheRelayNeverTouchedTheVault()
    {
        var adminToken = await TokenAsync(_admin);
        var memberToken = await TokenAsync(_member);
        var headBefore = (await LogAsync()).Select(l => l.Sequence).DefaultIfEmpty(0).Max();
        var newcomer = RelayPeer.New();

        // Received while closed, in this order: a sealed event of the member, then the admin's revoke of that member, the admin's
        // admission of a new node, and an event of the member again (after the revoke: the member is suspended, so it is refused at once).
        var fromMember = SealedRelayHost.SealedEvent(_member, originSequence: 7);
        var revoke = SealedRelayHost.ControlEvent(_admin, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(_member.Id), originSequence: 8);
        var add = SealedRelayHost.ControlEvent(_admin, EventTypes.WhitelistAdd,
            new WhitelistAddPayload(newcomer.Id, "Newcomer", Convert.ToBase64String(newcomer.PublicKey), null, false), originSequence: 9);
        (await Push(memberToken, fromMember)).Applied.Should().Be(1);
        (await Push(adminToken, revoke, add)).Applied.Should().Be(2);
        (await Send(HttpMethod.Post, "/api/sync/events", memberToken, new[] { SealedRelayHost.SealedEvent(_member) })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the member was suspended at receipt");

        // A position report taken while closed, and one the peer cannot have read.
        (await Send(HttpMethod.Post, "/api/sync/report-position?sequence=0", adminToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // A row that a stale mirror let through: its sender was revoked before the close. The import must refuse it.
        var staleRow = SealedRelayHost.SealedEvent(_revokedBefore, originSequence: 3);
        var inbox = Resolve<RelayInbox>();
        await inbox.AppendAsync(staleRow, _revokedBefore.Id, 3);
        var served = (await inbox.ReadAfterAsync(0, 100)).Select(r => r.RelaySeq).ToList();
        served.Should().HaveCount(4).And.BeInAscendingOrder();
        served.Min().Should().BeGreaterThan(headBefore, "relay sequences continue above the head the node closed with");

        // The relay answered all of that from the boot store. The vault saw no event.
        (await LogAsync()).Select(l => l.Sequence).DefaultIfEmpty(0).Max().Should().Be(headBefore);

        // Unlock: the gate imports.
        var lifecycle = Resolve<ISealedRelayLifecycle>();
        _gate.State = VaultState.Opening;
        await lifecycle.OnOpenedAsync();
        _gate.State = VaultState.Importing;
        var result = await lifecycle.ImportAsync();

        result.Waiting.Should().Be(0);
        result.Refused.Should().Be(1, "the node revoked before the close is refused, as an unlocked node would have refused it");
        (result.Applied + result.Held).Should().Be(3);

        var log = (await LogAsync()).Where(l => l.Sequence > headBefore).ToList();
        log.Select(l => l.EventId).Should().Equal([fromMember.EventId, revoke.EventId, add.EventId], "every event once, in the order it was received");
        log.Select(l => l.Sequence).Should().OnlyContain(s => s > served.Max(), "numbering continues above every relay sequence served");
        (await LogAsync()).Select(l => l.EventId).Should().NotContain(staleRow.EventId);

        // The real rules ran, in order: the admission is in the whitelist, the revoke took effect.
        var whitelist = Resolve<IWhitelistRepository>();
        (await whitelist.GetByNodeIdAsync(newcomer.Id))!.Status.Should().Be("A");
        (await whitelist.GetByNodeIdAsync(_member.Id, includeDeleted: true))!.Status.Should().Be("R");

        // Inbox empty, reports merged, the quarantine has the refused row, the mirror is the sealed whitelist again.
        var summary = await inbox.GetSummaryAsync();
        (summary.Events, summary.Blobs).Should().Be((0, 0));
        (await inbox.ListReportsAsync()).Should().BeEmpty();
        (await Resolve<ISyncPushPositionRepository>().GetAsync(_admin.Id))!.ReportedSeq.Should().Be(0);
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>().GetAllAsync()).Should().Contain(q => q.EventId == staleRow.EventId);
        using (var c = Resolve<BootStore>().Connections.Open())
        {
            c.ExecuteScalar<string?>("SELECT status FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = _member.Id.ToString("D") }).Should().NotBe("S", "the suspension ended with the rewrite: the mirror is the sealed whitelist again");
            c.ExecuteScalar<string>("SELECT status FROM tbl_whitelist WHERE node_id = @id COLLATE NOCASE", new { id = newcomer.Id.ToString("D") }).Should().Be("A");
        }

        // A second import has nothing to do: no event is applied twice.
        _gate.State = VaultState.Open;
        (await lifecycle.ImportAsync()).Should().Be(new RelayImportResult(0, 0, 0, 0, 0));
        (await LogAsync()).Where(l => l.Sequence > headBefore).Should().HaveCount(3);
    }
}
