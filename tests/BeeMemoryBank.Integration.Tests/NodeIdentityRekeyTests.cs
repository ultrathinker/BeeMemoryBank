using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The node identity re-key on a full node (release 2.12, plan WS-D): the operation through its API, its refusals, the transition chain of the
/// handshake, and a peer that re-keyed: only its new key authenticates, and the sessions of its old key are gone.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class NodeIdentityRekeyTests : IAsyncLifetime
{
    // This node is a superadmin in its peers' view (a confirm is superadmin-only; with no peer to ask, the real provider says no).
    private readonly BmbWebApplicationFactory _factory = new SuperadminFactory();

    private sealed class SuperadminStanding : IOwnStandingProvider
    {
        public Task<bool> IsSuperadminAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class SuperadminFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IOwnStandingProvider>(new SuperadminStanding()));
        }
    }
    private HttpClient _client = null!;
    private const string Password = "rekeyTestPwd1!";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<NodeIdentity> IdentityAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    private async Task AddPeerAsync(Guid id, byte[] key, bool rekeyCapable, string name = "Laptop")
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = key, Status = "A", IsSuperadmin = true, Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            LastCapabilities = string.Join(",", rekeyCapable ? MetadataCapabilities.Advertised : MetadataCapabilities.Advertised.Where(c => c != MetadataCapabilities.IdentityRekeyV1)),
        });
    }

    private async Task<HttpResponseMessage> AuthenticateAsync(Guid nodeId, byte[] seed, IReadOnlyList<NodeKeyLinkDto>? chain = null)
    {
        var challenge = await (await _client.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        var serverNodeId = challenge.GetProperty("serverNodeId").GetGuid();
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(serverNodeId.ToByteArray()).Concat(Convert.FromBase64String(challengeB64)).ToArray();
        return await _client.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeId, ChallengeB64 = challengeB64, SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(seed, payload)),
            ProtocolVersion = SyncProtocolVersion.Current,
            KeyChain = chain,
        });
    }

    [Fact]
    public async Task A_rekey_writes_the_event_the_new_identity_row_and_the_history_and_the_handshake_carries_the_chain()
    {
        var before = await IdentityAsync();
        List<Guid> ownBefore;
        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            ownBefore = (await c.QueryAsync<string>("SELECT event_id FROM tbl_event WHERE node_id = @id COLLATE NOCASE",
                new { id = before.NodeId.ToString("D") })).Select(Guid.Parse).ToList();

        var resp = await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var after = await IdentityAsync();
        after.NodeId.Should().Be(before.NodeId, "the node keeps its id");
        after.Ed25519PublicKey.Should().NotEqual(before.Ed25519PublicKey);
        after.Ed25519PrivateKeyV.Should().Be(1, "the new seed is wrapped under the current master key");

        using (var scope = _factory.Services.CreateScope())
        {
            // The node signs with the new key from now on.
            var signed = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(after, [1, 2, 3]);
            Ed25519Signer.Verify(after.Ed25519PublicKey, [1, 2, 3], signed).Should().BeTrue();
            var history = await scope.ServiceProvider.GetRequiredService<NodeKeyHistoryStore>().ListAsync(after.NodeId);
            var row = history.Should().ContainSingle().Subject;
            row.Counter.Should().Be(1);
            row.PublicKey.Should().Equal(before.Ed25519PublicKey);
            row.NewPublicKey.Should().Equal(after.Ed25519PublicKey);
            var rotation = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetByIdAsync(row.RotationEventId.ToString("D"));
            rotation!.EventType.Should().Be(EventTypes.NodeKeyRotated);
            Ed25519Signer.Verify(before.Ed25519PublicKey, EventSignature.BuildPayload(rotation), rotation.Signature)
                .Should().BeTrue("the rotation is the node's last event under its old key");
            EventIdSet.Unpack(row.OwnEventIds).Should().BeEquivalentTo(ownBefore, "the node's own events since its checkpoint are listed");
        }

        var identity = await (await _client.GetAsync("/api/sync/identity")).Content.ReadFromJsonAsync<JsonElement>(Json);
        identity.GetProperty("ed25519PublicKeyB64").GetString().Should().Be(Convert.ToBase64String(after.Ed25519PublicKey));
        var link = identity.GetProperty("keyChain")[0];
        var walked = NodeKeyTransition.Advance(after.NodeId, before.Ed25519PublicKey, [new NodeKeyLink(
            Convert.FromBase64String(link.GetProperty("oldPublicKeyB64").GetString()!), Convert.FromBase64String(link.GetProperty("newPublicKeyB64").GetString()!),
            link.GetProperty("counter").GetInt64(), Convert.FromBase64String(link.GetProperty("sigByOldB64").GetString()!),
            Convert.FromBase64String(link.GetProperty("sigByNewB64").GetString()!))]);
        walked.Should().Equal(after.Ed25519PublicKey, "a caller that pinned the old key walks the chain to the new one");

        var page = await (await _client.GetAsync("/api/node/identity")).Content.ReadFromJsonAsync<JsonElement>(Json);
        page.GetProperty("fingerprint").GetString().Should().Be(NodeKeyTransition.Fingerprint(after.Ed25519PublicKey));
        page.GetProperty("counter").GetInt64().Should().Be(1);
        page.GetProperty("history").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_crash_inside_the_rekey_transaction_leaves_the_old_key_whole_and_the_next_rekey_runs()
    {
        // The event, the identity row and the history row are one transaction: a crash before its commit (here: its last write fails) leaves no
        // rotation event, no history row and the old seed in place, so the node keeps signing with its old key and every peer still trusts it.
        var before = await IdentityAsync();
        var db = _factory.Services.GetRequiredService<DbConnectionFactory>();
        using (var c = db.CreateConnection())
            await c.ExecuteAsync("CREATE TRIGGER p11_crash_at_history BEFORE INSERT ON tbl_node_key_history BEGIN SELECT RAISE(ABORT, 'crash'); END");

        using (var scope = _factory.Services.CreateScope())
            await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "test"))
                .Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();

        var after = await IdentityAsync();
        after.Ed25519PublicKey.Should().Equal(before.Ed25519PublicKey);
        after.Ed25519PrivateKey.Should().Equal(before.Ed25519PrivateKey, "the old wrapped seed is still the one on record");
        after.Ed25519PrivateKeyIV.Should().Equal(before.Ed25519PrivateKeyIV);
        using (var c = db.CreateConnection())
        {
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_type = @t", new { t = EventTypes.NodeKeyRotated }))
                .Should().Be(0, "no rotation event outlives the transaction");
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_node_key_history")).Should().Be(0);
            await c.ExecuteAsync("DROP TRIGGER p11_crash_at_history");
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var signed = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(after, [4, 5, 6]);
            Ed25519Signer.Verify(before.Ed25519PublicKey, [4, 5, 6], signed).Should().BeTrue("the node still signs with its old key");

            // Nothing is left held (the key-operation lock, the event-write gate): the re-key runs once the cause is gone, as rotation 1.
            var result = await scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "test");
            result.Done.Should().BeTrue(result.Refusal);
            result.Counter.Should().Be(1);
        }
        (await IdentityAsync()).Ed25519PublicKey.Should().NotEqual(before.Ed25519PublicKey);
    }

    [Fact]
    public async Task A_rekey_is_refused_while_a_peer_cannot_follow_it_and_while_another_key_operation_runs()
    {
        var before = await IdentityAsync();
        var (oldPeerKey, _) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(Guid.NewGuid(), oldPeerKey, rekeyCapable: false, name: "Old phone");

        var refused = await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Old phone");

        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_whitelist SET last_capabilities = @caps", new { caps = string.Join(",", MetadataCapabilities.Advertised) });
        var dbId = _factory.Services.GetRequiredService<DbConnectionFactory>().DatabaseId;
        using (var rotation = KeyOperationLock.TryEnter(dbId, "a key rotation"))
        {
            rotation.Should().NotBeNull();
            var busy = await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" });
            busy.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await busy.Content.ReadAsStringAsync()).Should().Contain("a key rotation");
        }
        (await IdentityAsync()).Ed25519PublicKey.Should().Equal(before.Ed25519PublicKey, "nothing changed");
    }

    /// <summary>
    /// the refusals say what the administrator can do. A node whose identity key is outside its database is not
    /// sent to a "change it on that device" that exists nowhere; a node over the cap of listed events names the peer that holds compaction back.
    /// </summary>
    [Fact]
    public async Task The_refusals_say_what_the_administrator_can_do()
    {
        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            // Two peers that can follow a re-key: one has caught up, the other is far behind (never sent anything).
            await AddPeerAsync(Guid.NewGuid(), Ed25519Signer.GenerateKeyPair().publicKey, rekeyCapable: true, name: "Fast phone");
            var slow = Guid.NewGuid();
            await AddPeerAsync(slow, Ed25519Signer.GenerateKeyPair().publicKey, rekeyCapable: true, name: "Old laptop");
            await c.ExecuteAsync("UPDATE tbl_whitelist SET last_capabilities = @caps", new { caps = string.Join(",", MetadataCapabilities.Advertised) });
            var me = (await IdentityAsync()).NodeId.ToString("D");
            await c.ExecuteAsync(
                @"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i <= 50000)
                  INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, created_at)
                  SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))), 2) || '-a'
                         || substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))), @me, 1000 + i, 'snapshot_checkpoint', '{}', zeroblob(64), 'now'
                  FROM n", new { me });
            await c.ExecuteAsync("INSERT INTO tbl_sync_push_position (remote_node_id, last_pushed_seq, pushed_at) SELECT node_id, 999999, 'now' FROM tbl_whitelist WHERE display_name = 'Fast phone'");
        }
        var overCap = await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" });
        overCap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var said = await overCap.Content.ReadAsStringAsync();
        said.Should().Contain("50000").And.Contain("Old laptop", "the peer furthest behind is the one to wait for or remove").And.NotContain("Fast phone");

        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_node_identity SET ed25519_private_key_v = 2");
        var external = await (await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" })).Content.ReadAsStringAsync();
        external.Should().NotContain("on that device").And.Contain("new node");
    }

    /// <summary>the node's data folder reaches the service, so the offline re-key's lock refuses it (plan A6).</summary>
    [Fact]
    public async Task A_rekey_is_refused_while_an_offline_rekey_of_this_vault_holds_its_lock()
    {
        var before = await IdentityAsync();
        var dataPath = _factory.Services.GetRequiredService<BeeMemoryBank.Api.Services.SnapshotService>().DataPath;
        using (var offline = BeeMemoryBank.AppPaths.RekeyLock.TryAcquire(dataPath))
        {
            offline.Should().NotBeNull();
            var refused = await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" });
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("offline re-key");
        }
        (await IdentityAsync()).Ed25519PublicKey.Should().Equal(before.Ed25519PublicKey, "nothing changed");
    }

    [Fact]
    public async Task A_peer_that_rekeyed_authenticates_only_with_its_new_key_and_its_old_sessions_are_dropped()
    {
        var peer = Guid.NewGuid();
        var (oldKey, oldSeed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, oldKey, rekeyCapable: true);
        var oldSession = await AuthenticateAsync(peer, oldSeed);
        oldSession.StatusCode.Should().Be(HttpStatusCode.OK);
        var oldToken = (await oldSession.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;

        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        long lamport;
        using (var scope = _factory.Services.CreateScope())
            lamport = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync() + 10;
        var (rotation, entry) = NodeKeyRotationEvents.Rotation(peer, oldKey, b => Ed25519Signer.Sign(oldSeed, b), newSeed, 1, lamport, 0, [], "test",
            SyncProtocolVersion.Current, "Laptop");
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied);

        (await AuthenticateAsync(peer, oldSeed)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the retired key cannot open a session");
        (await AuthenticateAsync(peer, newSeed)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?after=0");
        stale.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", oldToken);
        (await _client.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session of the old key was dropped");
        entry.NewPublicKey.Should().NotBeEmpty();
    }

    /// <summary>
    /// A peer that re-keyed and whose rotation this node has not applied yet signs with its new key and sends its chain (plan WS-D D4): the chain leads
    /// from the key on record to the key that signed, so it is let in (and pushes the rotation next). A chain the key on record did not sign, no chain,
    /// or a node in an identity conflict is refused; nothing on the whitelist row moves.
    /// </summary>
    [Fact]
    public async Task A_peer_whose_rekey_is_not_applied_yet_authenticates_with_its_new_key_through_its_signed_chain_and_only_so()
    {
        var peer = Guid.NewGuid();
        var (oldKey, oldSeed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, oldKey, rekeyCapable: true);
        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (_, entry) = NodeKeyRotationEvents.Rotation(peer, oldKey, b => Ed25519Signer.Sign(oldSeed, b), newSeed, 1, 100, 0, [], "test",
            SyncProtocolVersion.Current, "Laptop");
        var (_, attackerSeed) = Ed25519Signer.GenerateKeyPair();
        var (_, forged) = NodeKeyRotationEvents.Rotation(peer, oldKey, b => Ed25519Signer.Sign(attackerSeed, b), newSeed, 1, 100, 0, [], "test",
            SyncProtocolVersion.Current, "Laptop");

        (await AuthenticateAsync(peer, newSeed)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a new key without a chain is not this peer");
        (await AuthenticateAsync(peer, newSeed, [NodeKeyLinkDto.Of(forged.Link)])).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a link the key on record did not sign");
        (await AuthenticateAsync(peer, newSeed, [NodeKeyLinkDto.Of(entry.Link)])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuthenticateAsync(peer, oldSeed, [NodeKeyLinkDto.Of(entry.Link)])).StatusCode.Should().Be(HttpStatusCode.OK,
            "the key on record still opens a session until the rotation is applied");

        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peer))!.Ed25519PublicKey
                .Should().Equal(oldKey, "only the node_key_rotated event moves the row");
            using var c = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
            await NodeKeyHistoryStore.RecordConflictAsync(c, null, peer, 1, oldKey, Guid.NewGuid(), entry.NewPublicKey, Guid.NewGuid(), forged.NewPublicKey);
        }
        (await AuthenticateAsync(peer, newSeed, [NodeKeyLinkDto.Of(entry.Link)])).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a node in an identity conflict is let in by the key on record only");
    }

    /// <summary>
    /// The phone pairing records that name this node carry its new key after a re-key (plan WS-D D4): the producer key in a record whose producer is
    /// this node, and the pairing signature by this node's new key, so that a restore of the phone's backups checks it against the key the network knows.
    /// </summary>
    [Fact]
    public async Task A_rekey_signs_the_phone_pairing_records_that_name_this_node_again_with_the_new_key()
    {
        var before = await IdentityAsync();
        var phone = Guid.NewGuid();
        var (otherNodeKey, _) = Ed25519Signer.GenerateKeyPair();
        var other = Guid.NewGuid();
        var mine = new BlindPhoneBackupSeal(Enumerable.Repeat((byte)3, 32).ToArray(), before.NodeId, before.Ed25519PublicKey,
            "https://hub.example.org:5300", new string('A', 43), before.NodeId, new byte[64]);
        var notMine = new BlindPhoneBackupSeal(Enumerable.Repeat((byte)4, 32).ToArray(), other, otherNodeKey,
            "https://other.example.org:5300", new string('A', 43), other, new byte[64]);
        using (var scope = _factory.Services.CreateScope())
        {
            var signer = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>();
            mine = mine with { PairingSignature = signer.SignChallenge(before, mine.PairingStatement(phone)) };
            var secrets = scope.ServiceProvider.GetRequiredService<SealedSecretService>();
            await secrets.PublishAsync(SealedSecretService.AndroidBackupName(phone), mine.Encode());
            await secrets.PublishAsync(SealedSecretService.AndroidBackupName(other), notMine.Encode());
        }

        (await _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "test" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await IdentityAsync();

        var resealed = await OpenSealAsync(SealedSecretService.AndroidBackupName(phone));
        resealed.ProducerPublicKey.Should().Equal(after.Ed25519PublicKey, "a restored phone dials and checks the producer by its current key");
        resealed.PairedBy.Should().Be(after.NodeId);
        Ed25519Signer.Verify(after.Ed25519PublicKey, resealed.PairingStatement(phone), resealed.PairingSignature).Should().BeTrue();
        resealed.BackupKey.Should().Equal(mine.BackupKey, "the backup key itself is unchanged");
        (await OpenSealAsync(SealedSecretService.AndroidBackupName(other))).Should().BeEquivalentTo(notMine, "a record that does not name this node is left as it is");
    }

    [Fact]
    public async Task A_crash_right_after_the_rekey_commit_is_finished_at_the_next_unlock_and_only_once()
    {
        // After its commit the re-key writes the audit line, signs the phone pairing records again with the new key and announces the new
        // fingerprint. A crash right after the commit left all three undone for good: the phone's backups stopped opening (the pairing check
        // uses the current key) and nothing said why. The next unlock finishes them, once.
        var before = await IdentityAsync();
        var phone = Guid.NewGuid();
        var name = SealedSecretService.AndroidBackupName(phone);
        var mine = new BlindPhoneBackupSeal(Enumerable.Repeat((byte)3, 32).ToArray(), before.NodeId, before.Ed25519PublicKey,
            "https://hub.example.org:5300", new string('A', 43), before.NodeId, new byte[64]);
        using (var scope = _factory.Services.CreateScope())
        {
            mine = mine with { PairingSignature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(before, mine.PairingStatement(phone)) };
            await scope.ServiceProvider.GetRequiredService<SealedSecretService>().PublishAsync(name, mine.Encode());
        }
        var db = _factory.Services.GetRequiredService<DbConnectionFactory>();
        async Task<long> AuditLinesAsync()
        {
            using var c = db.CreateConnection();
            return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'node_identity_rekeyed'");
        }
        async Task<long> AnnouncementsOfAsync(byte[] key)
        {
            using var c = db.CreateConnection();
            return await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_event WHERE event_type = @t AND node_id = @n COLLATE NOCASE AND payload LIKE @fp",
                new { t = EventTypes.NodeState, n = before.NodeId.ToString("D"), fp = "%" + NodeStateDraft.IdentityFingerprint(key) + "%" });
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var rekey = scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>();
            rekey.AfterCommitForTests = () => throw new IOException("the process died here");
            await FluentActions.Awaiting(() => rekey.RekeyAsync("test", "test")).Should().ThrowAsync<IOException>();
        }
        var after = await IdentityAsync();
        after.Ed25519PublicKey.Should().NotEqual(before.Ed25519PublicKey, "precondition: the re-key committed");
        (await OpenSealAsync(name)).ProducerPublicKey.Should().Equal(before.Ed25519PublicKey, "precondition: the record still names the old key");
        (await AuditLinesAsync()).Should().Be(0, "precondition: no audit line yet");
        (await AnnouncementsOfAsync(after.Ed25519PublicKey)).Should().Be(0, "precondition: the new fingerprint is not announced yet");

        // The next unlock (a restarted node is opened the same way).
        (await _client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        await _factory.Services.GetRequiredService<SessionService>().PostUnlockCatchUp;

        var resealed = await OpenSealAsync(name);
        resealed.ProducerPublicKey.Should().Equal(after.Ed25519PublicKey);
        Ed25519Signer.Verify(after.Ed25519PublicKey, resealed.PairingStatement(phone), resealed.PairingSignature).Should().BeTrue();
        (await AuditLinesAsync()).Should().Be(1);
        (await AnnouncementsOfAsync(after.Ed25519PublicKey)).Should().BeGreaterThan(0);

        // Done once: another unlock writes nothing more.
        var announced = await AnnouncementsOfAsync(after.Ed25519PublicKey);
        (await _client.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password })).EnsureSuccessStatusCode();
        await _factory.Services.GetRequiredService<SessionService>().PostUnlockCatchUp;
        (await AuditLinesAsync()).Should().Be(1);
        (await AnnouncementsOfAsync(after.Ed25519PublicKey)).Should().Be(announced);
    }

    /// <summary>A pairing record of a phone that this node paired and produces for, signed by its current key; its record name.</summary>
    private async Task<(Guid Phone, string Name)> PairedPhoneAsync()
    {
        var self = await IdentityAsync();
        var phone = Guid.NewGuid();
        var name = SealedSecretService.AndroidBackupName(phone);
        var seal = new BlindPhoneBackupSeal(Enumerable.Repeat((byte)5, 32).ToArray(), self.NodeId, self.Ed25519PublicKey,
            "https://hub.example.org:5300", new string('A', 43), self.NodeId, new byte[64]);
        using var scope = _factory.Services.CreateScope();
        seal = seal with { PairingSignature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(self, seal.PairingStatement(phone)) };
        await scope.ServiceProvider.GetRequiredService<SealedSecretService>().PublishAsync(name, seal.Encode());
        return (phone, name);
    }

    private async Task<long> FollowUpMarkersAsync()
    {
        using var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key LIKE @p",
            new { p = NodeIdentityRekeyService.FollowUpMarkerPrefix + "%" });
    }

    /// <summary>A re-key whose process dies right after its commit: the steps after it are left to the next unlock.</summary>
    private async Task CrashedRekeyAsync(string reason)
    {
        using var scope = _factory.Services.CreateScope();
        var rekey = scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>();
        rekey.AfterCommitForTests = () => throw new IOException("the process died here");
        await FluentActions.Awaiting(() => rekey.RekeyAsync(reason, "test")).Should().ThrowAsync<IOException>();
    }

    private async Task FinishInterruptedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().FinishInterruptedFollowUpAsync();
    }

    [Fact]
    public async Task A_rekey_that_comes_while_an_interrupted_rekey_is_finished_never_leaves_the_pairing_records_on_a_retired_key()
    {
        // The unlock's catch-up finishes an interrupted re-key (key 1) while a new re-key (key 2) can come at any time - the identity page, or a
        // removal's background pass. Without the vault's key-operation lock the catch-up went on with key 1 after key 2 had committed and done
        // its own steps: it signed the pairing records again with the retired key 1 and removed its marker, so nothing was left to repair them.
        var (phone, name) = await PairedPhoneAsync();
        await CrashedRekeyAsync("first");
        (await FollowUpMarkersAsync()).Should().Be(1, "precondition: the first re-key's steps after its commit are still to do");

        NodeRekeyResult? during = null;
        using (var resumeScope = _factory.Services.CreateScope())
        {
            var resume = resumeScope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>();
            resume.BeforeResealForTests = async () =>
            {
                if (during is not null) return;
                using var other = _factory.Services.CreateScope();
                during = await other.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("second", "test");
            };
            await resume.FinishInterruptedFollowUpAsync();
        }
        during.Should().NotBeNull("the second re-key came while the first one's steps ran");
        if (!during!.Done)
        {
            during.Refusal.Should().Contain("is running on this node", "the vault's key operation is taken: the second re-key waits for it");
            using var later = _factory.Services.CreateScope();
            (await later.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("second", "test")).Done.Should().BeTrue();
        }

        var current = await IdentityAsync();
        var record = await OpenSealAsync(name);
        record.ProducerPublicKey.Should().Equal(current.Ed25519PublicKey, "the pairing record names the key the node has now");
        Ed25519Signer.Verify(current.Ed25519PublicKey, record.PairingStatement(phone), record.PairingSignature).Should().BeTrue(
            "signed by the current key, not a retired one");
        (await FollowUpMarkersAsync()).Should().Be(0, "both re-keys' steps are done");
    }

    [Fact]
    public async Task An_interrupted_rekey_is_not_finished_while_an_offline_rekey_or_another_key_operation_holds_the_vault()
    {
        await PairedPhoneAsync();
        await CrashedRekeyAsync("first");
        var dataPath = _factory.Services.GetRequiredService<BeeMemoryBank.Api.Services.SnapshotService>().DataPath;
        var databaseId = _factory.Services.GetRequiredService<DbConnectionFactory>().DatabaseId;

        using (var offline = BeeMemoryBank.AppPaths.RekeyLock.TryAcquire(dataPath))
        {
            offline.Should().NotBeNull();
            await FinishInterruptedAsync();
            (await FollowUpMarkersAsync()).Should().Be(1, "an offline re-key holds the vault: the steps wait for the next unlock");
        }
        using (var rotation = KeyOperationLock.TryEnter(databaseId, "a key rotation"))
        {
            rotation.Should().NotBeNull();
            await FinishInterruptedAsync();
            (await FollowUpMarkersAsync()).Should().Be(1, "another key operation runs: the steps wait for the next unlock");
        }

        await FinishInterruptedAsync();
        (await FollowUpMarkersAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_pairing_record_that_could_not_be_signed_again_keeps_the_rekeys_steps_for_the_next_unlock()
    {
        // The re-seal of one record fails (here: its row cannot be written). That record keeps the old key, so the steps after the re-key are not
        // done: the marker stays and the next unlock signs it again. Before, a per-record failure was only logged and the marker went.
        var (phone, name) = await PairedPhoneAsync();
        var db = _factory.Services.GetRequiredService<DbConnectionFactory>();
        var row = name.ToLowerInvariant();
        using (var c = db.CreateConnection())
        {
            await c.ExecuteAsync($"CREATE TRIGGER p11_reseal_fails_insert BEFORE INSERT ON tbl_sealed_secret WHEN NEW.name = '{row}' BEGIN SELECT RAISE(ABORT, 'locked'); END");
            await c.ExecuteAsync($"CREATE TRIGGER p11_reseal_fails_update BEFORE UPDATE ON tbl_sealed_secret WHEN NEW.name = '{row}' BEGIN SELECT RAISE(ABORT, 'locked'); END");
        }

        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "test")).Done.Should().BeTrue();
        var after = await IdentityAsync();
        (await OpenSealAsync(name)).ProducerPublicKey.Should().NotEqual(after.Ed25519PublicKey, "precondition: that record could not be signed again");
        (await FollowUpMarkersAsync()).Should().Be(1, "a record that failed keeps the steps for the next unlock");

        using (var c = db.CreateConnection())
        {
            await c.ExecuteAsync("DROP TRIGGER p11_reseal_fails_insert");
            await c.ExecuteAsync("DROP TRIGGER p11_reseal_fails_update");
        }
        await FinishInterruptedAsync();

        var record = await OpenSealAsync(name);
        record.ProducerPublicKey.Should().Equal(after.Ed25519PublicKey);
        Ed25519Signer.Verify(after.Ed25519PublicKey, record.PairingStatement(phone), record.PairingSignature).Should().BeTrue();
        (await FollowUpMarkersAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_audit_line_that_only_quotes_the_fingerprint_in_its_reason_does_not_stand_for_the_rekeys_own_line()
    {
        // The steps after a re-key write its audit line only when it is not there yet. The check matched the fingerprint anywhere in the text, and
        // the text ends with a free reason: a line whose reason merely quoted the fingerprint counted as the re-key's own line.
        await CrashedRekeyAsync("first");
        var self = await IdentityAsync();
        var fingerprint = NodeKeyTransition.Fingerprint(self.Ed25519PublicKey);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAuditLogRepository>().LogAsync("node", self.NodeId.ToString("D"), "node_identity_rekeyed", "test",
                $"New identity key, rotation 7, fingerprint 0000; reason: replaces the key with fingerprint {fingerprint}");

        await FinishInterruptedAsync();

        using var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'node_identity_rekeyed' AND details LIKE @head",
                new { head = $"New identity key, rotation 1, fingerprint {fingerprint}%" }))
            .Should().Be(1, "the re-key's own line is written once");
        (await FollowUpMarkersAsync()).Should().Be(0);
    }

    /// <summary>
    /// A peer announced two keys for the same rotation (plan WS-D D2): its events are refused until a superadmin's device confirms one. The confirm
    /// names one of the two rotations; anything else, this node's own conflict, or a conflict that is not open is refused with a sentence.
    /// </summary>
    [Fact]
    public async Task A_superadmin_settles_an_identity_conflict_by_confirming_one_of_the_two_keys_and_nothing_else()
    {
        var peer = Guid.NewGuid();
        var (oldKey, oldSeed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, oldKey, rekeyCapable: true);
        long lamport;
        using (var scope = _factory.Services.CreateScope())
            lamport = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync() + 10;
        var (_, seedA) = Ed25519Signer.GenerateKeyPair();
        var (_, seedB) = Ed25519Signer.GenerateKeyPair();
        var (rotationA, entryA) = NodeKeyRotationEvents.Rotation(peer, oldKey, b => Ed25519Signer.Sign(oldSeed, b), seedA, 1, lamport, 0, [], "a",
            SyncProtocolVersion.Current, "Laptop");
        var (rotationB, entryB) = NodeKeyRotationEvents.Rotation(peer, oldKey, b => Ed25519Signer.Sign(oldSeed, b), seedB, 1, lamport + 1, 0, [], "b",
            SyncProtocolVersion.Current, "Laptop");
        using (var scope = _factory.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            (await applier.ApplyAsync(rotationA)).Should().Be(EventApplyResult.Applied);
            await applier.ApplyAsync(rotationB);
        }
        var page = await (await _client.GetAsync("/api/node/identity")).Content.ReadFromJsonAsync<JsonElement>(Json);
        page.GetProperty("conflicts").GetArrayLength().Should().Be(1, "two keys for one rotation is a conflict");

        var self = await IdentityAsync();
        (await ConfirmAsync(peer, Guid.NewGuid())).Should().Contain("not one of the two");
        (await ConfirmAsync(self.NodeId, rotationB.EventId)).Should().Contain("its own key");

        var ok = await _client.PostAsJsonAsync($"/api/node/identity/conflicts/{peer}/confirm", new { rotationEventId = rotationB.EventId });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peer))!.Ed25519PublicKey
                .Should().Equal(entryB.NewPublicKey, "the confirmed key is the peer's key now");
            (await scope.ServiceProvider.GetRequiredService<NodeKeyHistoryStore>().GetConflictAsync(peer))!.IsOpen.Should().BeFalse();
            var confirm = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetByIdAsync(
                (await ok.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("confirmEventId").GetGuid().ToString("D")))!;
            confirm.EventType.Should().Be(EventTypes.NodeKeyConfirm, "the confirm goes to the network");
            Ed25519Signer.Verify(self.Ed25519PublicKey, EventSignature.BuildPayload(confirm), confirm.Signature).Should().BeTrue();
        }
        (await ConfirmAsync(peer, rotationB.EventId)).Should().Contain("no open identity conflict");
        entryA.NewPublicKey.Should().NotEqual(entryB.NewPublicKey);
    }

    private async Task<string> ConfirmAsync(Guid nodeId, Guid rotationEventId)
    {
        var resp = await _client.PostAsJsonAsync($"/api/node/identity/conflicts/{nodeId}/confirm", new { rotationEventId });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<BlindPhoneBackupSeal> OpenSealAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        using var c = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var row = await c.QuerySingleAsync<(byte[] Wrapped, byte[] Iv)>("SELECT wrapped, iv FROM tbl_sealed_secret WHERE name = @name", new { name });
        var dek = scope.ServiceProvider.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            BlindPhoneBackupSeal.TryDecode(SealedSecretCrypto.TryOpen(name, row.Wrapped, row.Iv, dek), out var seal).Should().BeTrue();
            return seal!;
        }
        finally
        {
            Array.Clear(dek);
        }
    }

    /// <summary>
    /// A re-keyed peer keeps its id and its network settings here: its whitelist row (address, TLS pin, name, ownership) and the addresses a
    /// person typed for it stay; only the failures its channels noted (a probe that met the new key before the rotation arrived) are forgotten.
    /// </summary>
    [Fact]
    public async Task A_peer_that_rekeyed_keeps_its_id_its_address_its_pin_and_the_addresses_typed_for_it()
    {
        var peer = Guid.NewGuid();
        var (oldKey, oldSeed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, oldKey, rekeyCapable: true);
        const string manual = "https://192.168.1.50:5300";
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await repo.GetByNodeIdAsync(peer))!;
            row.ApiAddress = "https://laptop.example.org:5300";
            row.TlsSpki = new string('B', 43);
            await repo.UpdateAsync(row);
            var channels = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.Channels.PeerChannelStore>();
            (await channels.AddManualAsync(peer, manual, DateTime.UtcNow)).Should().BeTrue();
            await channels.NoteFailAsync(peer, manual, BeeMemoryBank.Sync.Channels.ChannelSources.Manual, BeeMemoryBank.Sync.Channels.ChannelScopes.Lan,
                BeeMemoryBank.Sync.Channels.ChannelFailures.WrongPeer, DateTime.UtcNow);
        }
        var before = await WhitelistRowAsync(peer);

        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        long lamport;
        using (var scope = _factory.Services.CreateScope())
            lamport = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync() + 10;
        var (rotation, entry) = NodeKeyRotationEvents.Rotation(peer, oldKey, b => Ed25519Signer.Sign(oldSeed, b), newSeed, 1, lamport, 0, [], "test",
            SyncProtocolVersion.Current, "Laptop");
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied);

        var after = await WhitelistRowAsync(peer);
        after.NodeId.Should().Be(peer);
        after.Ed25519PublicKey.Should().Equal(entry.NewPublicKey);
        after.ApiAddress.Should().Be(before.ApiAddress);
        after.TlsSpki.Should().Be(before.TlsSpki);
        after.DisplayName.Should().Be(before.DisplayName);
        after.Ownership.Should().Be(before.Ownership);
        after.IsSuperadmin.Should().Be(before.IsSuperadmin);
        after.Status.Should().Be("A");
        using var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var channel = await c.QuerySingleOrDefaultAsync<(string Address, long FailStreak)>(
            "SELECT address, fail_streak FROM tbl_peer_channel WHERE peer_node_id = @id COLLATE NOCASE AND source = 'manual'", new { id = peer.ToString("D") });
        channel.Address.Should().Be(manual, "an address a person typed for the peer is a setting, kept across its re-key");
        channel.FailStreak.Should().Be(0, "the failures noted against its old key are forgotten");
    }

    private async Task<WhitelistEntry> WhitelistRowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(id))!;
    }

    /// <summary>
    /// an own event being written when the re-key starts (an announcer between signing with the old key and
    /// appending) is either on the rotation's list or signed by the new key - never an old-key event that falls between the list and the commit.
    /// The re-key quiesces event authoring and reads its list inside the commit transaction.
    /// </summary>
    [Fact]
    public async Task An_own_event_written_while_the_rekey_starts_is_on_the_rotations_list()
    {
        var before = await IdentityAsync();
        SyncEvent racing;
        Task<HttpResponseMessage> rekey;
        using (await EventWriteGate.Instance.EnterAdmissionAsync())
        {
            // The announcer's write: inside the gate, signed by the key it read (the old one), not appended yet.
            using (var scope = _factory.Services.CreateScope())
            {
                racing = new SyncEvent
                {
                    EventId = Guid.NewGuid(), NodeId = before.NodeId, EventType = EventTypes.SnapshotCheckpoint, Payload = "{\"cp\":1}",
                    LamportTs = scope.ServiceProvider.GetRequiredService<ILamportClock>().Tick(), ProtocolVersion = SyncProtocolVersion.Current,
                    CreatedAt = DateTime.UtcNow,
                };
                racing.EntityId = EventEntityId.Derive(racing);
                racing.Signature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(before, EventSignature.BuildPayload(racing));
            }
            rekey = _client.PostAsJsonAsync("/api/node/identity/rekey", new { reason = "race" });
            await Task.Delay(500);
            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendAsync(racing);
        }
        (await rekey).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var row = (await scope.ServiceProvider.GetRequiredService<NodeKeyHistoryStore>().ListAsync(before.NodeId)).Should().ContainSingle().Subject;
            EventIdSet.Unpack(row.OwnEventIds).Should().Contain(racing.EventId, "the write that was in flight is the node's own, listed");
        }
    }

    /// <summary>the identity page lists the retired-key attention marks, and an administrator acknowledges one.</summary>
    [Fact]
    public async Task A_retired_key_attention_mark_is_listed_and_acknowledged_on_the_identity_page()
    {
        var peer = Guid.NewGuid();
        var (key, _) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, key, rekeyCapable: true, name: "Old laptop");
        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await c.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@k, '2', '2026-11-17T00:00:00Z')",
                new { k = EventApplier.RetiredKeyAttentionMarkerPrefix + peer.ToString("D") });

        var page = await (await _client.GetAsync("/api/node/identity")).Content.ReadFromJsonAsync<JsonElement>(Json);
        var mark = page.GetProperty("retiredKeyAttention").EnumerateArray().Should().ContainSingle().Subject;
        mark.GetProperty("displayName").GetString().Should().Be("Old laptop");
        mark.GetProperty("count").GetInt64().Should().Be(2);

        (await _client.PostAsync($"/api/node/identity/attention/{peer}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        page = await (await _client.GetAsync("/api/node/identity")).Content.ReadFromJsonAsync<JsonElement>(Json);
        page.GetProperty("retiredKeyAttention").GetArrayLength().Should().Be(0);
        (await _client.PostAsync($"/api/node/identity/attention/{peer}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_envelope_endpoint_answers_only_an_authenticated_peer_and_only_for_a_rotation_of_this_node()
    {
        var unknownCommit = Guid.NewGuid();
        (await _client.PostAsync($"/api/sync/dek-envelope/{unknownCommit}", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var peer = Guid.NewGuid();
        var (key, seed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, key, rekeyCapable: true);
        var token = (await (await AuthenticateAsync(peer, seed)).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sync/dek-envelope/{unknownCommit}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Weak test: the envelope is built for the caller's CURRENT key (a peer that re-keyed opens it with its new seed, never with
    /// the retired one), a revoked or blind caller gets 403, and a rotation that is no longer this node's current key gets 409.
    /// </summary>
    [Fact]
    public async Task The_envelope_is_built_for_the_callers_current_key_and_refused_to_revoked_and_blind_callers_and_for_a_stale_rotation()
    {
        var me = await IdentityAsync();
        var current = Guid.NewGuid();
        var stale = Guid.NewGuid();
        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            foreach (var (id, epoch) in new[] { (current, me.DekEpoch), (stale, me.DekEpoch - 1) })
                await c.ExecuteAsync(
                    @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, created_at)
                      VALUES (@id, @node, 1, @type, @payload, zeroblob(64), @now)",
                    new
                    {
                        now = DateTime.UtcNow.ToString("O"), id = id.ToString("D"), node = me.NodeId.ToString("D"), type = EventTypes.DekRotationCommit,
                        payload = JsonSerializer.Serialize(new DekRotationCommitPayload(Guid.NewGuid().ToString("D"), epoch, "now", me.NodeId.ToString("D"))),
                    });

        // A peer that re-keyed: this node holds its new key; it authenticates with that key.
        var peer = Guid.NewGuid();
        var (oldKey, oldSeed) = Ed25519Signer.GenerateKeyPair();
        var (newKey, newSeed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(peer, newKey, rekeyCapable: true);
        var token = (await (await AuthenticateAsync(peer, newSeed)).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        Task<HttpResponseMessage> Ask(Guid commit, string bearer)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sync/dek-envelope/{commit}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            return _client.SendAsync(req);
        }

        var answer = await Ask(current, token);
        answer.StatusCode.Should().Be(HttpStatusCode.OK);
        var box = (await answer.Content.ReadFromJsonAsync<BeeMemoryBank.Sync.DekRotation.DekEnvelopeAnswer>())!;
        var opened = DekEnvelope.Open(box.EphemeralPublicKeyB64, box.WrappedB64, box.NonceB64, current, peer, newSeed);
        opened.Should().Equal(_factory.Services.GetRequiredService<SessionService>().GetMasterDek(), "it carries this node's current master key");
        FluentActions.Invoking(() => DekEnvelope.Open(box.EphemeralPublicKeyB64, box.WrappedB64, box.NonceB64, current, peer, oldSeed))
            .Should().Throw<Exception>("the retired key opens nothing");

        var staleAnswer = await Ask(stale, token);
        staleAnswer.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleAnswer.Content.ReadAsStringAsync()).Should().Contain("no longer this node's current key");

        // A blind caller, even whitelisted and authenticated.
        var blind = BlindNodeId.NewId();
        var (blindKey, blindSeed) = Ed25519Signer.GenerateKeyPair();
        await AddPeerAsync(blind, blindKey, rekeyCapable: true, name: "Blind");
        var blindToken = (await (await AuthenticateAsync(blind, blindSeed)).Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        (await Ask(current, blindToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The peer is revoked after it authenticated: its token is still in hand, the answer is not.
        using (var c = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R', deleted_at = 'now' WHERE node_id = @n COLLATE NOCASE", new { n = peer.ToString("D") });
        (await Ask(current, token)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
