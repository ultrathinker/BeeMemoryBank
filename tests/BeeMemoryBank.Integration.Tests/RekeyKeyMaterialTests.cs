using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Floor tests of L's steps of the offline re-key (rekey-offline.md §4): KeyMaterialStep, PeerRevokeStep and
/// EventLogResetStep, run by the real verb on a vault that holds every kind of key material and peer. The re-keyed
/// vault is then STARTED as a node, and what the old credentials can still do is tried on it.
/// <list type="bullet">
/// <item>auth: only the owner's password unlocks; another superadmin's password, a recovery code and an agent key
///   that could auto-unlock before do not;</item>
/// <item>auth: every old peer, blind nodes included, is revoked;</item>
/// <item>data loss: a write after the re-key sorts after every row the node already holds (its clock survives the
///   cleared log), and the node still signs with its own identity;</item>
/// <item>the report lists what was cleared and revoked.</item>
/// </list>
/// ChatRekeyStep (R2) is a double here: the chat key under D_c, chat.db emptied.
/// </summary>
public sealed class RekeyKeyMaterialTests : IAsyncLifetime
{
    private const string Password = "rekeyOwnerPw1";
    private const string Admin2Password = "rekeyAdmin2Pw1";
    private const long PlantedLamport = 1_000_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-rekey-km-" + Guid.NewGuid().ToString("N"));
    private string _d = "";
    private string _agentKey = "";
    private string _recoveryCode = "";
    private Guid _fullPeer, _blindPeer, _nodeId;
    private byte[] _publicKey = [];
    private long _oldLastSeq;

    public async Task InitializeAsync() => await BuildVaultAsync(_ => Task.CompletedTask);

    private async Task BuildVaultAsync(Func<SqliteConnection, Task> alter)
    {
        using var factory = new BmbWebApplicationFactory();
        await factory.InitializeNodeAsync("Owner", Password);
        var session = factory.Services.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();
        using var client = factory.CreateClient();
        int adminId;
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            adminId = (await sp.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;
            await sp.GetRequiredService<UserService>().CreateUserAsync("admin2", "Admin Two", Admin2Password, "superadmin");
            await sp.GetRequiredService<UserService>().CreateUserAsync("reader", "Reader", "rekeyReaderPw1", "user");
            _recoveryCode = await sp.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
            await sp.GetRequiredService<ArticleService>().CreateAsync("Kept", "/Notes", [], "body");
            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            _fullPeer = Guid.NewGuid();
            _blindPeer = BlindNodeId.NewId();
            foreach (var (id, name) in new[] { (_fullPeer, "Laptop"), (_blindPeer, "Blind") })
                await whitelist.CreateAsync(new WhitelistEntry
                {
                    NodeId = id, DisplayName = name, Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
                    Status = "A", IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
            var identity = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            (_nodeId, _publicKey) = (identity.NodeId, identity.Ed25519PublicKey);
        }

        // Before the re-key, each old credential the tests later refuse does open this vault.
        foreach (var credential in new[] { Admin2Password, _recoveryCode })
        {
            session.Lock();
            (await session.UnlockAsync(credential)).Should().BeTrue("before the re-key this credential unlocks");
        }

        // An agent of the owner: it carries the master key and unlocks the vault by itself.
        client.DefaultRequestHeaders.Add("X-User-Id", adminId.ToString());
        var created = await client.PostAsJsonAsync("/api/agents", new { name = "owner-agent" });
        created.EnsureSuccessStatusCode();
        _agentKey = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        session.Lock();
        await AgentRequestAsync(factory);
        session.IsUnlocked.Should().BeTrue("before the re-key the agent key unlocks the vault by itself");

        var dek = session.GetMasterDek();
        using (var conn = factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            // The OS auto-unlock slot, a retired master key with its chain, a recovery box, and what the event log
            // positions: quarantine and a pull position. One row carries a Lamport timestamp above the whole log.
            var (osWrapped, osIv) = MasterKeyManager.WrapMasterDek(dek, RandomNumberGenerator.GetBytes(32));
            await conn.ExecuteAsync(
                "INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, created_at) VALUES ('os_auto_unlock', @osWrapped, @osIv, @now)",
                new { osWrapped, osIv, now = DateTime.UtcNow.ToString("o") });
            var retiredName = IRetiredMasterDekStore.KeyNamePrefix + Guid.NewGuid();
            var (rw, riv) = NodeDataKeyEnvelope.Wrap(retiredName, RandomNumberGenerator.GetBytes(32), dek);
            await conn.ExecuteAsync("INSERT INTO tbl_node_data_key (key_name, wrapped_key, iv, created_at) VALUES (@retiredName, @rw, @riv, @now)",
                new { retiredName, rw, riv, now = DateTime.UtcNow.ToString("o") });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at, chain_encrypted_new_dek, chain_iv)
                  VALUES (@id, 'Applied', @now, @now, @now, 'chain', 'iv')", new { id = Guid.NewGuid().ToString(), now = DateTime.UtcNow.ToString("o") });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_recovery_box (box_id, kind, author_node_id, dek_fingerprint, kdf_preset, salt, wrapped, iv, created_at, status)
                  VALUES (@b, 'strong', @n, @fp, 's512t6', @salt, @w, @iv, @now, 'A')",
                new
                {
                    b = Guid.NewGuid().ToString(), n = _nodeId.ToString(), fp = DekFingerprint.Of(dek), salt = new byte[32],
                    w = RandomNumberGenerator.GetBytes(49), iv = new byte[12], now = DateTime.UtcNow.ToString("o")
                });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_sync_quarantine (event_id, event_type, origin_node_id, failure_count, first_failed_at_utc, last_failed_at_utc, last_error)
                  VALUES (@e, 'article_create', @n, 1, @now, @now, 'planted')", new { e = Guid.NewGuid().ToString(), n = _fullPeer.ToString(), now = DateTime.UtcNow });
            await conn.ExecuteAsync("INSERT INTO tbl_sync_position (remote_node_id, last_sequence_num, updated_at) VALUES (@n, 5, @now)",
                new { n = _fullPeer.ToString(), now = DateTime.UtcNow });
            await conn.ExecuteAsync("UPDATE tbl_article SET lamport_ts = @PlantedLamport", new { PlantedLamport });
            await alter((SqliteConnection)conn);
            _oldLastSeq = await conn.ExecuteScalarAsync<long>("SELECT MAX(sequence_num) FROM tbl_event");
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        CryptographicOperations.ZeroMemory(dek);
        var chat = Path.Combine(factory.DataPath, RekeyRunner.ChatDb);
        if (File.Exists(chat))
        {
            using var c = new SqliteConnection($"Data Source={chat};Pooling=False");
            c.Open();
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();

        _d = Path.Combine(_root, "vault-" + Guid.NewGuid().ToString("N")[..8]);
        CopyTree(factory.DataPath, _d);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------ tests

    [Fact]
    public async Task AfterTheReKey_OnlyTheOwnersPasswordUnlocks_AndNoOldCredentialOrPeerIsLeft()
    {
        var outcome = await RunAsync();
        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);

        using var node = new NodeAt(_d);
        var session = node.Services.GetRequiredService<SessionService>();
        session.IsUnlocked.Should().BeFalse();

        await AgentRequestAsync(node);
        session.IsUnlocked.Should().BeFalse("the old agent key carries no key any more");
        (await session.UnlockAsync(Admin2Password)).Should().BeFalse("another superadmin's slot is gone");
        (await session.UnlockAsync(_recoveryCode)).Should().BeFalse("the recovery code's slot is gone");
        (await session.UnlockAsync(Password)).Should().BeTrue("the owner's password opens the new vault");

        using (var scope = node.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
                .Should().BeEmpty("every old peer, the blind node included, is revoked");
            var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            identity.NodeId.Should().Be(_nodeId);
            identity.Ed25519PublicKey.Should().Equal(_publicKey, "the node keeps its identity");
        }
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_d, RekeyRunner.MainDb)};Mode=ReadOnly;Pooling=False");
        conn.Open();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type <> 'user'").Should().Be(0);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_user WHERE key_slot_id IS NOT NULL").Should().Be(1, "only the owner keeps a slot");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_sync_quarantine").Should().Be(0);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_sync_position").Should().Be(0);
    }

    [Fact]
    public async Task AWriteAfterTheReKey_SortsAfterEveryRowTheNodeHolds_AndIsSignedByTheNode()
    {
        var outcome = await RunAsync();
        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);

        using var node = new NodeAt(_d);
        (await node.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
        Guid written;
        using (var scope = node.Services.CreateScope())
            written = (await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("After", "/Notes", [], "new body")).Id;

        using var conn = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var lamport = await conn.ExecuteScalarAsync<long>("SELECT lamport_ts FROM tbl_article WHERE id = @written COLLATE NOCASE", new { written = written.ToString() });
        lamport.Should().BeGreaterThan(PlantedLamport,
            "the node's clock starts from the log, and the log's checkpoint carries a timestamp above every row");

        var events = (await conn.QueryAsync<(long Seq, string Type, long Lamport)>(
            "SELECT sequence_num, event_type, lamport_ts FROM tbl_event ORDER BY sequence_num")).ToList();
        events[0].Type.Should().Be("snapshot_checkpoint", "the log starts again from a fresh checkpoint");
        events[0].Seq.Should().BeGreaterThan(_oldLastSeq, "new events number above the old log");
        (await conn.ExecuteScalarAsync<long>("SELECT MAX(cp_after) FROM tbl_compaction_log"))
            .Should().Be(_oldLastSeq, "a peer asking for the old log is sent to a snapshot");
        using var scope2 = node.Services.CreateScope();
        foreach (var e in await scope2.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0))
            Ed25519Signer.Verify(_publicKey, BeeMemoryBank.Sync.EventSignature.BuildPayload(e), e.Signature)
                .Should().BeTrue($"event {e.EventType} is signed with the node's own identity");
    }

    [Fact]
    public async Task TheReport_ListsTheClearedSlotsAndAgents_AndTheRevokedPeers()
    {
        var outcome = await RunAsync();
        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);

        var report = JsonSerializer.Deserialize<RekeyReport>(await File.ReadAllTextAsync(Path.Combine(_d, RekeyRunner.ReportFile)), RekeyReport.Json)!;
        report.ClearedSlots.Should().Contain(s => s.EndsWith(" admin2")).And.Contain(s => s.EndsWith(" recovery"))
            .And.Contain(s => s.EndsWith(" os_auto_unlock"));
        report.ClearedAgents.Should().ContainSingle().Which.Should().EndWith(" owner-agent");
        report.RevokedPeers.Should().BeEquivalentTo([$"{_fullPeer} Laptop", $"{_blindPeer} Blind"]);
    }

    [Fact]
    public async Task ALegacyPlaintextIdentitySeed_IsSealedUnderTheNewKey()
    {
        // A node from before identity sealing: its seed is stored in the clear (v=0).
        await DisposeAsync();
        await BuildVaultAsync(async conn =>
        {
            var row = await conn.QuerySingleAsync<(byte[] Pk, byte[] Iv)>("SELECT ed25519_private_key, ed25519_private_key_iv FROM tbl_node_identity");
            var dekRow = await conn.QuerySingleAsync<(byte[] W, byte[] Iv, byte[] Salt, long Mem, long It, long Par)>(
                "SELECT encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot WHERE slot_type = 'user' ORDER BY slot_id LIMIT 1");
            var dek = MasterKeyManager.UnwrapMasterDek(dekRow.W, dekRow.Iv, KeyDerivation.DeriveKek(Password, dekRow.Salt, (int)dekRow.Mem, (int)dekRow.It, (int)dekRow.Par));
            var seed = NodeIdentityCrypto.GetDecryptedPrivateKey(row.Pk, row.Iv, 1, _nodeId, dek);
            await conn.ExecuteAsync("UPDATE tbl_node_identity SET ed25519_private_key = @seed, ed25519_private_key_iv = NULL, ed25519_private_key_v = 0", new { seed });
        });

        var outcome = await RunAsync();
        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);

        using var conn2 = new SqliteConnection($"Data Source={Path.Combine(_d, RekeyRunner.MainDb)};Mode=ReadOnly;Pooling=False");
        conn2.Open();
        var id = conn2.QuerySingle<(byte[] Pk, byte[] Iv, long V)>("SELECT ed25519_private_key, ed25519_private_key_iv, ed25519_private_key_v FROM tbl_node_identity");
        id.V.Should().Be(1, "the seed is no longer stored in the clear");
        var slot = conn2.QuerySingle<(byte[] W, byte[] Iv, byte[] Salt, long Mem, long It, long Par)>(
            "SELECT encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot");
        var dc = MasterKeyManager.UnwrapMasterDek(slot.W, slot.Iv, KeyDerivation.DeriveKek(Password, slot.Salt, (int)slot.Mem, (int)slot.It, (int)slot.Par));
        NodeIdentityCrypto.PublicKeyOf(NodeIdentityCrypto.GetDecryptedPrivateKey(id.Pk, id.Iv, 1, _nodeId, dc)).Should().Equal(_publicKey);
    }

    [Fact]
    public async Task ABlindNodesVault_IsRefused_AndLeftAsItWas()
    {
        // v=2: the identity key lives outside the database, as only a blind node keeps it.
        await DisposeAsync();
        await BuildVaultAsync(conn => conn.ExecuteAsync("UPDATE tbl_node_identity SET ed25519_private_key_v = 2"));
        var before = Tree(_d);

        var outcome = await RunAsync();

        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("v=2");
        Tree(_d).Should().BeEquivalentTo(before, "D is untouched");
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private Task<RekeyOutcome> RunAsync() =>
        RekeyRunner.RunAsync(new RekeyOptions
        {
            DataDir = _d,
            OwnerPassword = Password,
            Steps = [new KeyMaterialStep(), new RowResealStep(), new ChatDouble(), new DerivedDataClearStep(), new PeerRevokeStep(), new EventLogResetStep()],
            Preflight = new PreflightDouble(),
            ChatTables = ChatTablesAllCleared(),
        });

    private async Task AgentRequestAsync(BmbWebApplicationFactory node)
    {
        using var agent = node.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _agentKey);
        await agent.GetAsync("/api/session/status");
    }

    /// <summary>A node started on the given data directory: the re-keyed vault, as its first start opens it.</summary>
    private sealed class NodeAt(string dataDir) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BeeMemoryBank:DataPath", dataDir);
        }
    }

    /// <summary>R2's ChatRekeyStep, reduced: the chat key under D_c, chat.db emptied.</summary>
    private sealed class ChatDouble : IRekeyStep
    {
        public string Name => "ChatRekey";

        public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
        {
            if (ctx.Chat != null)
                foreach (var t in (await ctx.Chat.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND sql NOT LIKE 'CREATE VIRTUAL%'")).ToList())
                    await ctx.Chat.ExecuteAsync($"DELETE FROM [{t}]");
            foreach (var name in (await ctx.Main.QueryAsync<string>("SELECT key_name FROM tbl_node_data_key")).ToList())
            {
                var (w, iv) = NodeDataKeyEnvelope.Wrap(name, name == ChatDataKeyEnvelope.KeyName ? ctx.Keys.ChatKey : RandomNumberGenerator.GetBytes(32), ctx.Keys.CampaignDek);
                await ctx.Main.ExecuteAsync("UPDATE tbl_node_data_key SET wrapped_key = @w, iv = @iv WHERE key_name = @name", new { w, iv, name });
            }
            return new(Name, new Dictionary<string, long>(), []);
        }

        public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) => Task.FromResult<IReadOnlyList<RekeyProblem>>([]);
    }

    private sealed class PreflightDouble : IRekeyPreflight
    {
        public Task<RekeyPreflightReport> RunAsync(string sourceDir, SqliteConnection liveMain, SqliteConnection? liveChat, RekeyKeys keys, CancellationToken ct) =>
            Task.FromResult(new RekeyPreflightReport([], [], 0));
    }

    private IReadOnlyDictionary<string, TableFate>? ChatTablesAllCleared()
    {
        var chat = Path.Combine(_d, RekeyRunner.ChatDb);
        if (!File.Exists(chat)) return null;
        using var c = RekeyRunner.OpenLive(chat);
        return c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToDictionary(n => n, _ => TableFate.Cleared, StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            if (f.EndsWith("-shm") || f.EndsWith("-wal") || Path.GetFileName(f) == "node.lock") continue;
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        }
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static Dictionary<string, string> Tree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
}
