using System.Security.Cryptography;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.DekRotation;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Two things a DEK rotation used to destroy that it had no reason to.
///
/// <para><b>Agents.</b> The rewrap ran <c>DELETE FROM tbl_agent</c>, so every agent key on the node
/// stopped working after every rotation — including every ordinary user's agent, which holds no key
/// material at all. Only agents that carry a wrapped master DEK can be affected by a rotation.</para>
///
/// <para><b>Node data keys.</b> Data a host keeps outside the vault database (the API's chat.db) is
/// sealed under a node data key whose wrapped form lives in <c>tbl_node_data_key</c>. The rewrap
/// must carry every such row forward, on the initiator and on a peer alike, and must not let one bad
/// row abort the rotation.</para>
/// </summary>
public class DekRewrapperAgentsAndNodeDataKeysTests : SyncTestFixture
{
    private async Task<int> InsertAgentAsync(string name, byte[]? encryptedDek, byte[]? iv, byte[]? salt, string status = "A")
    {
        using var conn = Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_agent (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
              VALUES (@name, 'bee_test', @hash, @enc, @iv, @kdf, @salt, @status, @now, 1);
              SELECT last_insert_rowid()",
            new
            {
                name,
                hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                enc = encryptedDek,
                iv,
                kdf = encryptedDek is null ? 0 : 1,
                salt,
                status,
                now = DateTime.UtcNow.ToString("O")
            });
    }

    private async Task<List<string>> AgentNamesAsync()
    {
        using var conn = Factory.CreateConnection();
        return (await conn.QueryAsync<string>("SELECT name FROM tbl_agent ORDER BY name")).ToList();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rotation_RemovesOnlyAgentsThatCarryTheMasterKey(bool isInitiator)
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);

        var oldDek = Session.GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (enc, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, oldDek);

        await InsertAgentAsync("auto-unlock (superadmin-owned)", enc, iv, salt);
        await InsertAgentAsync("ordinary user agent", null, null, null);
        await InsertAgentAsync("second ordinary agent", null, null, null);

        int? initiatorSlotId = null;
        byte[]? slotDek = null, slotIv = null;
        if (isInitiator)
        {
            using var conn = Factory.CreateConnection();
            initiatorSlotId = await conn.ExecuteScalarAsync<int>("SELECT slot_id FROM tbl_key_slot LIMIT 1");
            slotDek = await conn.ExecuteScalarAsync<byte[]>("SELECT encrypted_master_dek FROM tbl_key_slot WHERE slot_id = @id", new { id = initiatorSlotId });
            slotIv = await conn.ExecuteScalarAsync<byte[]>("SELECT iv FROM tbl_key_slot WHERE slot_id = @id", new { id = initiatorSlotId });
        }

        var (agentsDeleted, _, _) = await DekRewrapper.RewrapAllAsync(
            Factory, Session, oldDek, RandomNumberGenerator.GetBytes(32),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: isInitiator,
            initiatorSlotId: initiatorSlotId, newWrappedSlotDek: slotDek, newWrappedSlotIv: slotIv);

        agentsDeleted.Should().Be(1, "only the agent holding a wrapped copy of the old master DEK is affected by a rotation");
        (await AgentNamesAsync()).Should().BeEquivalentTo(["ordinary user agent", "second ordinary agent"]);
    }

    [Fact]
    public async Task Rotation_KeepsNoKeyAgentsResolvableByTheirKeyHash()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);

        var apiKey = AgentKeyHelper.GenerateApiKey();
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_agent (name, key_prefix, key_hash, kdf_version, status, created_at, owner_user_id)
                  VALUES ('teammate', @prefix, @hash, 0, 'A', @now, 1)",
                new { prefix = AgentKeyHelper.GetKeyPrefix(apiKey), hash = AgentKeyHelper.ComputeKeyHash(apiKey), now = DateTime.UtcNow.ToString("O") });
        }

        await DekRewrapper.RewrapAllAsync(
            Factory, Session, Session.GetMasterDek(), RandomNumberGenerator.GetBytes(32),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        // The exact lookup AgentAuthMiddleware performs for a presented bee_ key.
        var agentRepo = new BeeMemoryBank.Storage.Sqlite.AgentRepository(Factory);
        var resolved = await agentRepo.GetByKeyHashAsync(AgentKeyHelper.ComputeKeyHash(apiKey));
        resolved.Should().NotBeNull("an agent without key material has nothing a rotation could invalidate");
        resolved!.CanAutoUnlock.Should().BeFalse();
    }

    [Fact]
    public async Task Rotation_ReWrapsEveryNodeDataKey_AndToleratesRacedAndBrokenRows()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);

        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);

        var normalKey = NodeDataKeyEnvelope.Generate();
        var racedKey = NodeDataKeyEnvelope.Generate();
        using (var conn = Factory.CreateConnection())
        {
            void Insert(string name, (byte[] wrapped, byte[] iv) w) => conn.Execute(
                "INSERT INTO tbl_node_data_key (key_name, wrapped_key, iv, created_at) VALUES (@name, @wrapped, @iv, @now)",
                new { name, w.wrapped, w.iv, now = DateTime.UtcNow.ToString("O") });

            Insert("normal", NodeDataKeyEnvelope.Wrap("normal", normalKey, oldDek));
            // A key created after the sentinel moved: already sealed under the new DEK.
            Insert("raced", NodeDataKeyEnvelope.Wrap("raced", racedKey, newDek));
            // Sealed under a key nobody has.
            Insert("broken", NodeDataKeyEnvelope.Wrap("broken", NodeDataKeyEnvelope.Generate(), RandomNumberGenerator.GetBytes(32)));
        }

        var (_, _, tally) = await DekRewrapper.RewrapAllAsync(
            Factory, Session, (byte[])oldDek.Clone(), (byte[])newDek.Clone(),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        tally.AlreadyOnNewKey.Should().Be(1);
        tally.Unreadable.Should().Be(1, "a broken data key is reported, and does not roll the rotation back");
        tally.UnreadableExamples.Should().Contain("tbl_node_data_key:broken");

        using (var conn = Factory.CreateConnection())
        {
            var rows = (await conn.QueryAsync<(string Name, byte[] Wrapped, byte[] Iv)>(
                "SELECT key_name, wrapped_key, iv FROM tbl_node_data_key")).ToDictionary(r => r.Name);

            NodeDataKeyEnvelope.TryUnwrap("normal", rows["normal"].Wrapped, rows["normal"].Iv, newDek)
                .Should().Equal(normalKey, "the data key itself is unchanged, only re-sealed under the new master DEK");
            NodeDataKeyEnvelope.TryUnwrap("normal", rows["normal"].Wrapped, rows["normal"].Iv, oldDek)
                .Should().BeNull("nothing may stay sealed under the retired master DEK");
            NodeDataKeyEnvelope.TryUnwrap("raced", rows["raced"].Wrapped, rows["raced"].Iv, newDek)
                .Should().Equal(racedKey);
        }

        // The rotation completed: the session is on the new DEK.
        var current = Session.GetMasterDek();
        current.Should().Equal(newDek);
    }

    [Fact]
    public void NodeDataKeyEnvelope_IsBoundToItsName_AndRejectsLegacyFraming()
    {
        var masterDek = RandomNumberGenerator.GetBytes(32);
        var dataKey = NodeDataKeyEnvelope.Generate();
        var (wrapped, iv) = NodeDataKeyEnvelope.Wrap("chat", dataKey, masterDek);

        NodeDataKeyEnvelope.TryUnwrap("chat", wrapped, iv, masterDek).Should().Equal(dataKey);
        NodeDataKeyEnvelope.TryUnwrap("other", wrapped, iv, masterDek)
            .Should().BeNull("one row's wrapped key must not open as another name's key");
        NodeDataKeyEnvelope.TryUnwrap("chat", wrapped, iv, RandomNumberGenerator.GetBytes(32)).Should().BeNull();

        // A legacy v0 wrapped DEK (48 bytes, no AAD) must not be accepted in place of a data key.
        var (v0, v0Iv) = DekManager.WrapDekLegacyV0(dataKey, masterDek);
        NodeDataKeyEnvelope.TryUnwrap("chat", v0, v0Iv, masterDek).Should().BeNull();

        // An article DEK wrap (v1 framing, different AAD) must not open either.
        var (articleWrap, articleIv) = DekManager.WrapDek(dataKey, masterDek, "bmb-art-dek"u8.ToArray());
        NodeDataKeyEnvelope.TryUnwrap("chat", articleWrap, articleIv, masterDek).Should().BeNull();
    }

    // ───── Fix round 1 ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void NodeDataKeyEnvelope_MalformedInput_IsUnreadable_NeverAnException()
    {
        var masterDek = RandomNumberGenerator.GetBytes(32);
        var (wrapped, iv) = NodeDataKeyEnvelope.Wrap("chat", NodeDataKeyEnvelope.Generate(), masterDek);

        // AesGcm answers a wrong nonce size with ArgumentException, not CryptographicException.
        NodeDataKeyEnvelope.TryUnwrap("chat", wrapped, iv[..5], masterDek).Should().BeNull("invalid IV length");
        NodeDataKeyEnvelope.TryUnwrap("chat", wrapped, [.. iv, 0, 0, 0, 0], masterDek).Should().BeNull("oversized IV");
        NodeDataKeyEnvelope.TryUnwrap("chat", wrapped, null, masterDek).Should().BeNull("null IV");
        NodeDataKeyEnvelope.TryUnwrap("chat", null, iv, masterDek).Should().BeNull("null wrapped key");
        NodeDataKeyEnvelope.TryUnwrap("chat", wrapped[..20], iv, masterDek).Should().BeNull("truncated framing");
        var wrongVersion = (byte[])wrapped.Clone();
        wrongVersion[0] = 0x02;
        NodeDataKeyEnvelope.TryUnwrap("chat", wrongVersion, iv, masterDek).Should().BeNull("unknown version byte");
    }

    [Fact]
    public async Task Rotation_WithMalformedNodeDataKeyRows_CountsThemUnreadable_AndStillCompletes()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);

        var good = NodeDataKeyEnvelope.Generate();
        var (goodWrapped, goodIv) = NodeDataKeyEnvelope.Wrap("good", good, oldDek);
        var (w, iv) = NodeDataKeyEnvelope.Wrap("short-iv", NodeDataKeyEnvelope.Generate(), oldDek);
        using (var conn = Factory.CreateConnection())
        {
            const string sql = "INSERT INTO tbl_node_data_key (key_name, wrapped_key, iv, created_at) VALUES (@name, @wrapped, @iv, 'x')";
            await conn.ExecuteAsync(sql, new { name = "good", wrapped = goodWrapped, iv = goodIv });
            await conn.ExecuteAsync(sql, new { name = "short-iv", wrapped = w, iv = iv[..5] });
            await conn.ExecuteAsync(sql, new { name = "garbage", wrapped = new byte[] { 1, 2, 3 }, iv = new byte[] { 4 } });
        }

        var (_, _, tally) = await DekRewrapper.RewrapAllAsync(
            Factory, Session, (byte[])oldDek.Clone(), (byte[])newDek.Clone(),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        tally.Unreadable.Should().Be(2, "malformed rows are reported like undecryptable ones, not thrown");
        using (var conn = Factory.CreateConnection())
        {
            var row = await conn.QuerySingleAsync<(byte[] W, byte[] Iv)>(
                "SELECT wrapped_key, iv FROM tbl_node_data_key WHERE key_name = 'good'");
            NodeDataKeyEnvelope.TryUnwrap("good", row.W, row.Iv, newDek).Should().Equal(good);
        }
        Session.GetMasterDek().Should().Equal(newDek, "the rotation committed despite the malformed rows");
    }

    [Fact]
    public async Task Rotation_ReEncryptsRemoteAccountTokens_UnderTheNewKey()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);

        async Task InsertAsync(string id, byte[] token, byte[] iv)
        {
            using var conn = Factory.CreateConnection();
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_remote_account (id, display_name, base_url, remote_username, encrypted_token, token_iv, created_at, updated_at)
                  VALUES (@id, 'r', 'https://remote.example', 'u', @token, @iv, 'x', 'x')",
                new { id, token, iv });
        }

        var (t1, iv1) = RemoteAccountService.SealToken("bmbrt_token_one", oldDek);
        await InsertAsync("A1", t1, iv1);
        var (t2, iv2) = RemoteAccountService.SealToken("bmbrt_token_raced", newDek);
        await InsertAsync("A2", t2, iv2);
        var (t3, iv3) = RemoteAccountService.SealToken("bmbrt_token_lost", RandomNumberGenerator.GetBytes(32));
        await InsertAsync("A3", t3, iv3);

        var (_, _, tally) = await DekRewrapper.RewrapAllAsync(
            Factory, Session, (byte[])oldDek.Clone(), (byte[])newDek.Clone(),
            newEpoch: 2, commitEventId: Guid.NewGuid().ToString(), isInitiator: false);

        tally.AlreadyOnNewKey.Should().Be(1);
        tally.UnreadableExamples.Should().Contain("tbl_remote_account:A3");

        using var check = Factory.CreateConnection();
        var rows = (await check.QueryAsync<(string Id, byte[] Token, byte[] Iv)>(
            "SELECT id, encrypted_token, token_iv FROM tbl_remote_account")).ToDictionary(r => r.Id);
        RemoteAccountService.TryOpenToken(rows["A1"].Token, rows["A1"].Iv, newDek).Should().Be("bmbrt_token_one");
        RemoteAccountService.TryOpenToken(rows["A1"].Token, rows["A1"].Iv, oldDek).Should().BeNull(
            "nothing may stay sealed under the retired master DEK");
        RemoteAccountService.TryOpenToken(rows["A2"].Token, rows["A2"].Iv, newDek).Should().Be("bmbrt_token_raced");
    }

    private sealed class ThrowingHook(Exception ex) : IDekRotationHook
    {
        public Task BeforeRewrapAsync(CancellationToken ct) => Task.FromException(ex);
    }

    [Fact]
    public async Task PreRewrapHooks_AreMandatory_AnyFailureSurfacesAsAPreconditionFailure()
    {
        var io = new IOException("disk I/O error");
        var sp = new ServiceCollection().AddSingleton<IDekRotationHook>(new ThrowingHook(io)).BuildServiceProvider();
        var act = () => DekRewrapper.RunPreRewrapHooksAsync(sp, logger: null);
        (await act.Should().ThrowAsync<DekRotationPreconditionException>())
            .Which.InnerException.Should().BeSameAs(io);

        var own = new DekRotationPreconditionException("rows left");
        var sp2 = new ServiceCollection().AddSingleton<IDekRotationHook>(new ThrowingHook(own)).BuildServiceProvider();
        var act2 = () => DekRewrapper.RunPreRewrapHooksAsync(sp2, logger: null);
        (await act2.Should().ThrowAsync<DekRotationPreconditionException>()).Which.Should().BeSameAs(own);

        // No hooks registered (mobile, CLI): nothing to do.
        await DekRewrapper.RunPreRewrapHooksAsync(new ServiceCollection().BuildServiceProvider(), logger: null);
    }
}
