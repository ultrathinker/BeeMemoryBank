using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Step A on real nodes (docs/design/step-a-spec.md 4.4, 6, 16.2 items 6 and 8): a locked node holds what it is pushed
/// and applies it once, by itself, when it is unlocked; while anything is held the operations that would change the vault
/// or the log under it are refused with a reason; and a DEK rotation on an activated network makes the next metadata key
/// generation under the new DEK, which a holder of the old DEK alone cannot open.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class MetadataHeldEventsTests : IAsyncLifetime
{
    private const string MasterPassword = "metadataHeld1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _a = null!;
    private BmbWebApplicationFactory _b = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;

    public async Task InitializeAsync()
    {
        _a = new BmbWebApplicationFactory();
        _b = new BmbWebApplicationFactory();
        await _a.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _a.CreateClient();
        await UnlockAsync(_clientA);
        await _b.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _b.CreateClient();
        await UnlockAsync(_clientB);
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _a.Dispose();
        _b.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ALockedNode_HoldsWhatItIsPushed_AndAppliesItOnce_WhenItIsUnlocked()
    {
        await SyncAsync(_b, _clientA, _a);
        (await ActivateAsync(_clientA)).StatusCode.Should().Be(HttpStatusCode.OK);
        await SyncAsync(_b, _clientA, _a);
        _b.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue();

        (await _clientB.PostAsJsonAsync("/api/session/lock", new { })).EnsureSuccessStatusCode();
        var article = await CreateArticleAsync(_clientA, "Written while B was locked");
        await SyncAsync(_a, _clientB, _b); // A pushes to B: B accepts and holds, it cannot open anything now

        var held = (await StatusAsync(_clientB)).GetProperty("held");
        held.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        held.GetProperty("locked").GetInt32().Should().Be(held.GetProperty("count").GetInt32());
        held.GetProperty("message").GetString().Should().Contain("until this node is unlocked");
        (await ArticleCountAsync(_b, article)).Should().Be(0, "nothing sealed is applied while locked");

        // Unlock: the drain runs by itself, after the rotation retry of the post-unlock work.
        await UnlockAsync(_clientB);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await ArticleCountAsync(_b, article) == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        (await ArticleCountAsync(_b, article)).Should().Be(1, "the drain applied it at unlock");
        (await StatusAsync(_clientB)).GetProperty("held").GetProperty("count").GetInt32().Should().Be(0);

        using var conn = Db(_b);
        var rows = (await conn.QueryAsync<long>(
            "SELECT apply_state FROM tbl_event WHERE local_article_id = @Id COLLATE NOCASE", new { Id = article.ToString() })).ToList();
        rows.Should().ContainSingle("stored once").Which.Should().Be(EventApplyStates.Applied);
    }

    [Fact]
    public async Task WhileARowIsHeld_CompactionRotationRestoreAndActivation_AreRefused_AndTheStatusSaysWhy()
    {
        await HoldARowAsync(_a);

        var held = (await StatusAsync(_clientA)).GetProperty("held");
        held.GetProperty("count").GetInt32().Should().Be(1);
        held.GetProperty("key").GetInt32().Should().Be(1);
        held.GetProperty("message").GetString().Should().Contain("until their metadata key arrives");

        var compact = await _clientA.PostAsJsonAsync("/api/admin/compact", new { });
        compact.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await compact.Content.ReadAsStringAsync()).Should().Contain("Unlock it and wait");

        var rotate = await _clientA.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = MasterPassword });
        rotate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await rotate.Content.ReadAsStringAsync()).Should().Contain("Unlock it and wait");

        var restore = await _clientA.PostAsJsonAsync("/api/snapshots/restore-network",
            new { snapshotFileId = Guid.Empty, fileName = "any.bmbsnap", mode = "NetworkWide" });
        restore.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await restore.Content.ReadAsStringAsync()).Should().Contain("Unlock it and wait");

        var activate = await ActivateAsync(_clientA);
        activate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await activate.Content.ReadAsStringAsync()).Should().Contain("events_held");
    }

    [Fact]
    public async Task ADekRotation_OnTheInitiator_CreatesTheNextMetadataKey_ThatTheOldDekCannotOpen()
    {
        await SyncAsync(_b, _clientA, _a);
        (await ActivateAsync(_clientA)).StatusCode.Should().Be(HttpStatusCode.OK);
        var oldDek = _a.Services.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            var propose = await _clientA.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = MasterPassword });
            propose.StatusCode.Should().Be(HttpStatusCode.OK, await propose.Content.ReadAsStringAsync());
            var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("commitEventId").GetGuid().ToString();
            (await _clientA.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = MasterPassword }))
                .StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await WaitForRotationAsync(_clientA)).Should().Be("Completed");

            var keys = await KeyRowsAsync(_a);
            keys.Select(k => k.Generation).Should().Equal(1, 2);
            keys[1].DekFingerprint.Should().NotBe(keys[0].DekFingerprint, "generation 2 is wrapped under the new DEK");
            MetadataKeyCrypto.TryUnwrap(keys[0], [oldDek]).Should().NotBeNull("the control: the old DEK opens generation 1");
            MetadataKeyCrypto.TryUnwrap(keys[1], [oldDek]).Should().BeNull(
                "a node the rotation left out holds only the old DEK and cannot open what is sealed from now on");

            await CreateArticleAsync(_clientA, "Written after the rotation");
            using var conn = Db(_a);
            var newest = await conn.QuerySingleAsync<string>(
                "SELECT payload FROM tbl_event WHERE event_type = 'sealed' ORDER BY sequence_num DESC LIMIT 1");
            SealedEnvelope.Parse(newest).KeyId.Should().Be(keys[1].KeyId, "authoring picks the newest generation it can open");
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'metadata_key_set'")).Should().Be(2);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldDek);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>A sealed row from a peer, held for a key this node never got (what a missing metadata_key_set leaves).</summary>
    private static async Task HoldARowAsync(BmbWebApplicationFactory node)
    {
        var envelope = $"{{\"v\":1,\"k\":\"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}\"," +
                       $"\"n\":\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))}\",\"b\":[]," +
                       $"\"c\":\"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(80))}\"}}";
        using var scope = node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendAsync(new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = 10_000, EventType = EventTypes.Sealed,
            Payload = envelope, Signature = RandomNumberGenerator.GetBytes(64), ProtocolVersion = EventClasses.SealedProtocol,
            CreatedAt = DateTime.UtcNow,
        }, null, new EventRowLocal(ApplyState: EventApplyStates.Held, HoldReason: HoldReasons.Key, OriginSuperadmin: false));
    }

    private static async Task<List<MetadataKeyRow>> KeyRowsAsync(BmbWebApplicationFactory node)
    {
        using var conn = Db(node);
        return (await conn.QueryAsync<(string KeyId, long Generation, string Fp, byte[] Wrapped, byte[] Iv)>(
                "SELECT key_id, generation, dek_fingerprint, wrapped, iv FROM tbl_metadata_key ORDER BY generation"))
            .Select(r => new MetadataKeyRow(r.KeyId, (int)r.Generation, r.Fp, r.Wrapped, r.Iv)).ToList();
    }

    private static async Task<string?> WaitForRotationAsync(HttpClient client)
    {
        string? step = null;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await client.GetFromJsonAsync<JsonElement>("/api/dek-rotation/progress", JsonOpts);
            step = progress.GetProperty("currentStep").GetString();
            if (step is "Completed" or "Failed") return step;
            await Task.Delay(200);
        }
        return step;
    }

    private static async Task UnlockAsync(HttpClient client) =>
        (await client.PostAsJsonAsync("/api/session/unlock", new { password = MasterPassword })).EnsureSuccessStatusCode();

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/sync/activation", new { password = MasterPassword });

    private static async Task<JsonElement> StatusAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/sync/status", JsonOpts);

    private static async Task<Guid> CreateArticleAsync(HttpClient client, string title)
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new { title, treePath = "/Held", content = "body" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ArticleResponse>(JsonOpts))!.Id;
    }

    private static async Task<long> ArticleCountAsync(BmbWebApplicationFactory node, Guid id)
    {
        using var conn = Db(node);
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_article WHERE id = @Id COLLATE NOCASE", new { Id = id.ToString() });
    }

    private static async Task SyncAsync(BmbWebApplicationFactory node, HttpClient serverClient, BmbWebApplicationFactory server)
    {
        Guid serverId;
        using (var s = server.Services.CreateScope())
            serverId = (await s.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using var scope = node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(serverClient, "", serverId);
    }

    private static System.Data.IDbConnection Db(BmbWebApplicationFactory node) =>
        node.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
}
