using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
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
/// A0 wire hygiene (brief P1-A0): the sync envelope fields no receiver of any version reads —
/// actorType, actorName, viaAgentName and the derived entity_id — never leave the node. They are
/// not covered by the event signature (EventSignature.BuildPayload signs eight fields, not these),
/// every receiver overwrites or re-derives them on apply, and the only readers of the stored
/// values are local surfaces (activity feed, mobile status page), which keep working. A blind peer
/// also has no use for the full join snapshot: the join snapshot is a full node's starting vault,
/// the blind packages are the blind ones.
/// </summary>
public class WireHygieneTests : IAsyncLifetime
{
    private const string Password = "wireHygienePw1!";
    private const string CanaryActorType = "web";
    private const string CanaryActorName = "Canary Actor Name";
    private const string CanaryAgentName = "Canary Agent Name";
    private const string CanaryPath = "/Canary/Secret/Path";

    /// <summary>The JSON keys of the four removed fields — absent from the body, not merely null.</summary>
    private static readonly string[] RemovedKeys = ["actorType", "actorName", "viaAgentName", "entityId"];

    private readonly BmbWebApplicationFactory _factory = new();

    public async Task InitializeAsync() =>
        await _factory.InitializeNodeAsync(password: Password);

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ServedEvents_CarryNoActorFields()
    {
        await AppendCanaryRowAsync(_factory);

        using var http = await PeerClientAsync(Guid.NewGuid());
        var resp = await http.GetAsync("/api/sync/events?afterSequence=0");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertClean(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PushedEvents_CarryNoActorFields_AndThePeerStillAppliesThem()
    {
        var nodeB = new BmbWebApplicationFactory();
        try
        {
            using var clientA = _factory.CreateClient();
            (await clientA.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
                .EnsureSuccessStatusCode();
            await nodeB.JoinNodeAsync(clientA, "NodeB", Password);

            // An authored folder_create on B (a root folder, so nothing pre-exists on A and the
            // applier accepts it), with the canaries written into B's local rows.
            using (var scope = nodeB.Services.CreateScope())
            {
                var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
                var evt = new SyncEvent
                {
                    EventId = Guid.NewGuid(), NodeId = identity.NodeId, LamportTs = 1,
                    EventType = EventTypes.FolderCreate,
                    Payload = JsonSerializer.Serialize(new FolderCreatePayload(
                        Guid.NewGuid(), "/Canary", "Secret", null, DateTime.UtcNow, DateTime.UtcNow)),
                    ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow,
                };
                evt.Signature = Ed25519Signer.Sign(identity.Ed25519PrivateKey, EventSignature.BuildPayload(evt));
                await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendIfNotExistsAsync(evt);
            }
            await WriteCanaryActorFieldsAsync(nodeB);

            var capture = new CapturingPushHandler(_factory.Server.CreateHandler());
            using var wireA = new HttpClient(capture) { BaseAddress = _factory.Server.BaseAddress };
            Guid serverNodeId;
            using (var scope = _factory.Services.CreateScope())
                serverNodeId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            using (var scope = nodeB.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(wireA, "", serverNodeId);

            capture.PushBodies.Should().NotBeEmpty("B authored an event, so its cycle pushes");
            foreach (var body in capture.PushBodies)
                AssertClean(body);

            // And the push still works end to end: A applied the folder create.
            using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
                (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_folder WHERE path = '/Canary'"))
                    .Should().Be(1, "the push must survive without the envelope fields");
        }
        finally { nodeB.Dispose(); }
    }

    [Fact]
    public async Task ForJoin_RefusesABlindRequester_ButStillServesAFullJoiner()
    {
        // Signing the snapshot needs the identity key, and on a full node that key is wrapped by
        // the session — the same unlock a node that is actually serving joins has.
        using (var client = _factory.CreateClient())
            (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
                .EnsureSuccessStatusCode();

        using var blindHttp = await PeerClientAsync(BlindNodeId.NewId());
        (await blindHttp.GetAsync("/api/sync/snapshot/for-join")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a blind peer holds no key and no use for a join snapshot — its packages are the blind ones");

        using var fullHttp = await PeerClientAsync(Guid.NewGuid());
        var resp = await fullHttp.GetAsync("/api/sync/snapshot/for-join");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the control: a full joiner is still served");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/gzip");
        (await resp.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RestoreEvents_CarryNoActorFields()
    {
        var blind = new RecoveryTestFactory(blind: true);
        try
        {
            await blind.InitializeNodeAsync();
            await AppendCanaryRowAsync(blind);
            using var console = blind.CreateClient();
            using var issue = await console.PostAsync("/api/blind/restore-code", null);
            issue.EnsureSuccessStatusCode();
            var code = BlindRestoreCode.Parse(
                (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()).Secret;
            using var publicHttp = blind.Server.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/blind/restore/events");
            req.Headers.Add("X-Restore-Code", code);

            var resp = await publicHttp.SendAsync(req);

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertClean(await resp.Content.ReadAsStringAsync());
        }
        finally { blind.Dispose(); }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static void AssertClean(string body)
    {
        // Payload content (folder paths included) still travels — A0 removes the envelope fields
        // only, so a path value is asserted nowhere here; the canary names ride solely in the
        // envelope, and the removed keys must be absent, not merely null.
        body.Should().NotContain(CanaryActorName).And.NotContain(CanaryAgentName);
        foreach (var key in RemovedKeys)
            body.Should().NotContain($"\"{key}\"", $"{key} has no reader on any receiver and must not leave the node");
    }

    /// <summary>Appends a folder_create whose local row carries the canary actor fields — what a real
    /// node's tbl_event holds after a local change. Nothing applies it here, so the signature is a
    /// placeholder, as in SyncReportPositionTests.</summary>
    private static async Task AppendCanaryRowAsync(BmbWebApplicationFactory node)
    {
        await node.Services.GetRequiredService<IEventLogRepository>().AppendIfNotExistsAsync(new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = 1, EventType = EventTypes.FolderCreate,
            Payload = JsonSerializer.Serialize(new FolderCreatePayload(
                Guid.NewGuid(), CanaryPath, "Secret", null, DateTime.UtcNow, DateTime.UtcNow)),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow,
            Signature = new byte[64],
        });
        await WriteCanaryActorFieldsAsync(node);
    }

    private static async Task WriteCanaryActorFieldsAsync(BmbWebApplicationFactory node)
    {
        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_event SET actor_type = @t, actor_name = @n, via_agent_name = @a, entity_id = @e",
            new { t = CanaryActorType, n = CanaryActorName, a = CanaryAgentName, e = CanaryPath });
    }

    /// <summary>A whitelisted peer with a sync token, like SyncReportPositionTests's peer.</summary>
    private async Task<HttpClient> PeerClientAsync(Guid peerId)
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await _factory.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peerId, DisplayName = "peer", Ed25519PublicKey = publicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var http = _factory.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _factory.Services.GetRequiredService<SyncTokenStore>().IssueToken(peerId, SyncProtocolVersion.Current));
        return http;
    }

    /// <summary>Records the bodies a pushing SyncClient puts on the wire, then hands the request on.</summary>
    private sealed class CapturingPushHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> PushBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/sync/events")
                PushBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return await base.SendAsync(request, ct);
        }
    }
}
