using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using System.Security.Cryptography;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 3.4–3.5: an Api process in the blind role runs without the master DEK — its identity key is
/// a file outside the database, it still speaks sync, and everything that needs the DEK or a human
/// is simply not there.
/// </summary>
public class BlindRoleTests : IAsyncLifetime
{
    private readonly BlindNodeFactory _blind = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _blind.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FirstStart_CreatesABlindIdentity_WithTheKeyInA0600File()
    {
        var identity = await IdentityAsync(_blind);

        BlindNodeId.IsBlind(identity.NodeId).Should().BeTrue();
        identity.Ed25519PrivateKeyV.Should().Be(NodeIdentityCrypto.ExternalKeyVersion);
        identity.Ed25519PrivateKey.Should().BeEmpty("the seed must not be in the database at all");
        identity.Ed25519PrivateKeyIV.Should().BeNull();

        var keyPath = Path.Combine(_blind.DataPath, FileNodeKey.FileName);
        File.Exists(keyPath).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(keyPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        new FileNodeKey(keyPath).Matches(identity.Ed25519PublicKey).Should().BeTrue();
    }

    [Fact]
    public async Task IdentityAndChallenge_Work_ForAV2Node()
    {
        var identity = await IdentityAsync(_blind);
        using var http = _blind.Server.CreateClient();

        var id = await http.GetFromJsonAsync<JsonElement>("/api/sync/identity");
        id.GetProperty("nodeId").GetGuid().Should().Be(identity.NodeId);
        id.GetProperty("protocolVersion").GetInt32().Should().Be(SyncProtocolVersion.Current);

        var challenge = await http.PostAsync("/api/sync/challenge", null);
        challenge.StatusCode.Should().Be(HttpStatusCode.OK);
        (await challenge.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("serverNodeId").GetGuid().Should().Be(identity.NodeId);
    }

    /// <summary>
    /// The point of v=2: the blind node signs the sync handshake with its file key and no DEK, and
    /// a full node accepts it.
    /// </summary>
    [Fact]
    public async Task BlindNode_AuthenticatesToAFullNode_WithItsFileKey()
    {
        using var full = new BmbWebApplicationFactory();
        await full.InitializeNodeAsync(password: "blindAuthFullPw");
        var blindIdentity = await IdentityAsync(_blind);
        await full.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = blindIdentity.NodeId,
            DisplayName = "Blind",
            Ed25519PublicKey = blindIdentity.Ed25519PublicKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        var fullId = (await IdentityAsync(full)).NodeId;

        using var scope = _blind.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>();
        using var http = full.Server.CreateClient();
        var token = await PeerAuthenticator.AuthenticateAsync(
            signer, http, http.BaseAddress!.ToString(), blindIdentity, fullId);

        token.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("POST", "/api/session/unlock")]
    [InlineData("POST", "/api/join")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/api/articles")]
    [InlineData("POST", "/api/compaction/run")]
    [InlineData("GET", "/api/search?q=x")]
    public async Task DekAndNotesSurface_IsNotThere(string method, string path)
    {
        using var http = _blind.CreateClient();
        var resp = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { }) : null
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Review L-stage0 #1: an agent carrying a wrapped DEK (here slipped in after start) must not
    /// unlock a blind node's session by presenting its key — the agent middleware is not there.
    /// </summary>
    [Fact]
    public async Task AnAgentWithAWrappedDek_CannotUnlockTheBlindNode()
    {
        await IdentityAsync(_blind);
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (encryptedDek, iv) = AgentKeyHelper.EncryptDek(apiKey, RandomNumberGenerator.GetBytes(32));
        using (var scope = _blind.Services.CreateScope())
        {
            // The shape that can auto-unlock a full node: a superadmin's agent with a wrapped DEK.
            var ownerId = await scope.ServiceProvider.GetRequiredService<IUserRepository>().CreateAsync(new User
            {
                Username = "owner", DisplayName = "owner", PasswordHash = "unused", Role = UserRoles.Superadmin,
                IsActive = true, CreatedAt = DateTime.UtcNow
            });
            await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
            {
                Name = "stale agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey), EncryptedDek = encryptedDek, DekIV = iv,
                CreatedAt = DateTime.UtcNow, OwnerUserId = ownerId
            });
        }
        using var http = _blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        await http.GetAsync("/api/sync/identity");

        _blind.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse(
            "a blind node never holds the master DEK");
    }

    /// <summary>A full node never joins through a blind node; the join snapshot is not served there.</summary>
    [Fact]
    public async Task JoinSnapshot_Is404_EvenForAWhitelistedPeer()
    {
        await IdentityAsync(_blind);
        var peer = Guid.NewGuid();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        using var http = _blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _blind.Services.GetRequiredService<BeeMemoryBank.Api.Services.SyncTokenStore>().IssueToken(peer, BeeMemoryBank.Sync.SyncProtocolVersion.Current));

        (await http.GetAsync("/api/sync/snapshot/for-join")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sentinel_Is404_EvenIfOneLandedInTheDatabase()
    {
        await _blind.Services.GetRequiredService<INodeIdentityRepository>().StoreSentinelAsync(new byte[48]);
        using var http = _blind.Server.CreateClient();

        (await http.GetAsync("/api/sync/sentinel")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RestoreNetwork_FlagsReseed_WithoutAutoAcceptOnTheOriginatorsRow()
    {
        await IdentityAsync(_blind);
        var (hubId, hubKey) = await WhitelistSuperadminAsync(_blind);
        var payload = JsonSerializer.Serialize(new RestoreNetworkEventPayload(
            "00", DateTime.UtcNow.ToString("O"), 1, DateTime.UtcNow.AddHours(1).ToString("O"),
            "https://hub.example", FilterSecrets: true));
        var evt = Signed(hubId, hubKey, EventTypes.RestoreNetwork, payload);

        using (var scope = _blind.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt);

        // Set by the time the event is applied — not by a task that may never run.
        (await _blind.Services.GetRequiredService<BlindState>().GetReseedNeededAsync())
            .Should().Contain(evt.EventId.ToString());
    }

    /// <summary>
    /// Review L-stage0 #5: a rotation from a peer whose row keeps the default auto-accept (off)
    /// still leaves Committing on a blind node — there is no admin there to accept it.
    /// </summary>
    [Fact]
    public async Task RotationCommit_IsClosed_WithoutTheOriginatorsAutoAcceptFlag()
    {
        await IdentityAsync(_blind);
        var (hubId, hubKey) = await WhitelistSuperadminAsync(_blind);
        var now = DateTime.UtcNow.ToString("O");
        var proposed = Signed(hubId, hubKey, EventTypes.DekRotationProposed, JsonSerializer.Serialize(
            new DekRotationProposedPayload(2, now, DateTime.UtcNow.AddHours(1).ToString("O"), hubId.ToString())));
        var commit = Signed(hubId, hubKey, EventTypes.DekRotationCommit, JsonSerializer.Serialize(
            new DekRotationCommitPayload(proposed.EventId.ToString(), 2, now, hubId.ToString())));

        using (var scope = _blind.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            await applier.ApplyAsync(proposed);
            await applier.ApplyAsync(commit);
        }

        var states = _blind.Services.CreateScope().ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
        DekRotationState? state = null;
        for (var i = 0; i < 50 && state != DekRotationState.Applied; i++)
        {
            state = (await states.GetAsync(commit.EventId.ToString()))?.State;
            if (state != DekRotationState.Applied) await Task.Delay(100);
        }
        state.Should().Be(DekRotationState.Applied);
    }

    /// <summary>
    /// Review L-stage0 #9: a rotation that seals the new DEK for this blind node (an older or
    /// hand-configured node could build one) is refused, and never stored — the key file here opens it.
    /// </summary>
    [Theory]
    [InlineData(EventTypes.DekRotationProposed)]
    [InlineData(EventTypes.DekRotationCommit)]
    public async Task ARotationSealedForTheBlindNode_IsRefused_AndNotStored(string type)
    {
        var self = await IdentityAsync(_blind);
        var (hubId, hubKey) = await WhitelistSuperadminAsync(_blind);
        var now = DateTime.UtcNow.ToString("O");
        var envelopes = new DekEnvelopesPayload("ephemeral", new Dictionary<string, DekEnvelopeBox>
        {
            [self.NodeId.ToString().ToUpperInvariant()] = new("wrapped", "nonce")
        });
        var proposed = Signed(hubId, hubKey, EventTypes.DekRotationProposed, JsonSerializer.Serialize(
            new DekRotationProposedPayload(2, now, DateTime.UtcNow.AddHours(1).ToString("O"), hubId.ToString(),
                DekEnvelopes: type == EventTypes.DekRotationProposed ? envelopes : null)));
        var commit = Signed(hubId, hubKey, EventTypes.DekRotationCommit, JsonSerializer.Serialize(
            new DekRotationCommitPayload(proposed.EventId.ToString(), 2, now, hubId.ToString(), DekEnvelopes: envelopes)));
        var sealedOne = type == EventTypes.DekRotationProposed ? proposed : commit;

        using var scope = _blind.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
        if (sealedOne == commit) await applier.ApplyAsync(proposed);
        var act = () => applier.ApplyAsync(sealedOne);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(sealedOne.EventId))
            .Should().BeFalse("the envelope must not sit in this node's log");
    }

    [Fact]
    public async Task DekRotationApplier_IsTheBlindOne()
    {
        await IdentityAsync(_blind);
        using var scope = _blind.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDekRotationApplier>().Should().BeOfType<BlindDekRotationApplier>(
            "the full node's applier re-wraps the vault under the new DEK, which a blind node cannot open");
    }

    /// <summary>
    /// Plan 3.4: migrations need no DEK, so a blind node upgrades its schema on start like any other
    /// node. The database here is one migration behind (028, the first blind migration, undone).
    /// </summary>
    [Fact]
    public async Task BlindNode_MigratesFromThePreviousSchema_WithoutADek()
    {
        using var blind = new BlindNodeFactory();
        var keyPath = Path.Combine(blind.DataPath, FileNodeKey.FileName);
        Directory.CreateDirectory(blind.DataPath);
        var nodeId = BlindNodeId.NewId();
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = nodeId,
                DisplayName = "Blind",
                Ed25519PublicKey = new FileNodeKey(keyPath).Create(),
                Ed25519PrivateKey = [],
                Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion,
                CreatedAt = DateTime.UtcNow
            });
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(@"DROP TABLE tbl_blind_state;
                ALTER TABLE tbl_whitelist DROP COLUMN last_protocol_version;
                ALTER TABLE tbl_whitelist DROP COLUMN last_protocol_seen_at;
                DELETE FROM tbl_migration WHERE version = 28;");
        }

        var identity = await IdentityAsync(blind);

        identity.NodeId.Should().Be(nodeId);
        using var check = blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration WHERE version = 28")).Should().Be(1);
        (await check.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM pragma_table_info('tbl_whitelist') WHERE name = 'last_protocol_version'")).Should().Be(1);
    }

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node) =>
        (await node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

    private static async Task<(Guid Id, byte[] PrivateKey)> WhitelistSuperadminAsync(BmbWebApplicationFactory node)
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id,
            DisplayName = "Hub",
            Ed25519PublicKey = pub,
            Status = "A",
            IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return (id, priv);
    }

    private static SyncEvent Signed(Guid originator, byte[] privateKey, string type, string payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = originator,
            LamportTs = 5,
            EventType = type,
            Payload = payload,
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(privateKey, EventSignature.BuildPayload(evt));
        return evt;
    }
}
