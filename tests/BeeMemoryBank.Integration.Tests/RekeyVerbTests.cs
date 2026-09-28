using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Rekey.Steps;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The offline re-key run end to end (rekey-offline.md §2, §4), on a stopped copy of a real node's data directory:
/// the lock, pre-flight, copy, the steps (R1's real ones, doubles for L's key-material, peer and event-log steps and
/// R2's chat step), verify with the D1 check, scrub, report and swap. A refusal or a fault before the swap leaves D
/// byte-identical and in use, with no rekey-new left.
/// </summary>
public sealed class RekeyVerbTests : IAsyncLifetime
{
    private const string Password = "rekeyVerbPw1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-verb-" + Guid.NewGuid().ToString("N"));
    private string _d = "";
    private Dictionary<string, string> _tree = [];
    private Guid _article;
    private byte[] _bodyPlain = [];

    public async Task InitializeAsync()
    {
        using var factory = new BmbWebApplicationFactory();
        await factory.InitializeNodeAsync("Owner", Password);
        var session = factory.Services.GetRequiredService<SessionService>();
        (await session.UnlockAsync(Password)).Should().BeTrue();
        using (var scope = factory.Services.CreateScope())
        {
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            _article = (await articles.CreateAsync("Kept", "/Notes", [], "body v1")).Id;
            await articles.UpdateAsync(_article, plaintext: "body v2");
            await scope.ServiceProvider.GetRequiredService<CommentService>().CreateAsync(_article, "a comment");
            await scope.ServiceProvider.GetRequiredService<MediaService>().CreateAsync(
                "a.bin", "application/octet-stream", RandomNumberGenerator.GetBytes(1024), _article, isAttachment: true);
        }
        using (var conn = factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            var b = await conn.QuerySingleAsync<(byte[] W, byte[] DekIv, byte[] Iv, byte[] Ct)>(
                "SELECT b.encrypted_dek, b.dek_iv, b.iv, bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @a COLLATE NOCASE",
                new { a = _article.ToString() });
            var dek = session.GetMasterDek();
            _bodyPlain = EnvelopeFraming.Article.DecryptBody(_article, b.W, EnvelopeFraming.Article.UnwrapDek(_article, b.W, b.DekIv, dek), b.Ct, b.Iv);
        }
        var chat = Path.Combine(factory.DataPath, RekeyRunner.ChatDb);
        if (File.Exists(chat))
        {
            using var c = new SqliteConnection($"Data Source={chat};Pooling=False");
            c.Open();
            c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();

        // The node, stopped: a copy of its data directory without the transient SQLite side files.
        _d = Path.Combine(_root, "vault");
        CopyTree(factory.DataPath, _d);
        _tree = Tree(_d);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
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

    // ------------------------------------------------------------ doubles for the steps that are not R1's

    /// <summary>L's KeyMaterialStep, reduced to what the D1 check needs: D_c installed, old key material gone.</summary>
    private sealed class KeyMaterialDouble : IRekeyStep, IRekeyOwnerCredentialConsumer
    {
        private int _slot;
        private string _password = "";
        public string Name => "KeyMaterial";
        public void SetOwnerCredential(int ownerSlotId, string ownerPassword) => (_slot, _password) = (ownerSlotId, ownerPassword);

        public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
        {
            var db = ctx.Main;
            var dc = ctx.Keys.CampaignDek;
            var s = await db.QuerySingleAsync<(long Mem, long It, long Par)>(
                "SELECT argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot WHERE slot_id = @_slot", new { _slot });
            var salt = KeyDerivation.GenerateSalt();
            var kek = KeyDerivation.DeriveKek(_password, salt, (int)s.Mem, (int)s.It, (int)s.Par);
            var (w, iv) = MasterKeyManager.WrapMasterDek(dc, kek);
            await db.ExecuteAsync("UPDATE tbl_key_slot SET encrypted_master_dek = @w, iv = @iv, salt = @salt WHERE slot_id = @_slot", new { w, iv, salt, _slot });
            await db.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id <> @_slot", new { _slot });
            await db.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_id <> @_slot", new { _slot });
            await db.ExecuteAsync("UPDATE tbl_agent SET encrypted_dek = NULL, dek_iv = NULL");
            await db.ExecuteAsync("DELETE FROM tbl_node_data_key WHERE key_name LIKE 'retired-master-dek:%'");
            await db.ExecuteAsync("UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = NULL, chain_iv = NULL");
            foreach (var t in new[] { "tbl_recovery_box", "tbl_recovery_box_check", "tbl_recovery_box_pending_retire", "tbl_dek_retired_link" })
                await db.ExecuteAsync($"DELETE FROM {t}");
            var id = await db.QuerySingleAsync<(string NodeId, byte[] Pk, byte[]? Iv, long V)>(
                "SELECT node_id, ed25519_private_key, ed25519_private_key_iv, ed25519_private_key_v FROM tbl_node_identity LIMIT 1");
            if (id.V == 1)
            {
                var nodeId = Guid.Parse(id.NodeId);
                var seed = NodeIdentityCrypto.GetDecryptedPrivateKey(id.Pk, id.Iv, 1, nodeId, ctx.Keys.Predecessor);
                var (pk, pkIv) = NodeIdentityCrypto.EncryptPrivateKey(seed, dc, nodeId);
                await db.ExecuteAsync("UPDATE tbl_node_identity SET ed25519_private_key = @pk, ed25519_private_key_iv = @pkIv", new { pk, pkIv });
            }
            await db.ExecuteAsync("UPDATE tbl_node_identity SET sentinel_value = @s", new { s = MasterKeyManager.ComputeSentinel(dc) });
            return new(Name, new Dictionary<string, long>(), ["cleared-agent:none"]);
        }

        public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) => Task.FromResult<IReadOnlyList<RekeyProblem>>([]);
    }

    /// <summary>R2's ChatRekeyStep, reduced: chat rows dropped, every remaining node data key replaced under D_c.</summary>
    private sealed class ChatDouble(bool fail = false) : IRekeyStep
    {
        public string Name => "ChatRekey";

        public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
        {
            if (fail) throw new IOException("disk gone mid re-seal");
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

    private sealed class PeerRevokeDouble(Func<RekeyContext, Task>? sabotage = null) : IRekeyStep
    {
        public string Name => "PeerRevoke";

        public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
        {
            await ctx.Main.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id <> @n COLLATE NOCASE", new { n = ctx.NodeId.ToString() });
            if (sabotage != null) await sabotage(ctx);
            return new(Name, new Dictionary<string, long>(), ["revoked:none"]);
        }

        public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) => Task.FromResult<IReadOnlyList<RekeyProblem>>([]);
    }

    private sealed class EventLogResetDouble(bool verifyFails = false) : IRekeyStep
    {
        public string Name => "EventLogReset";

        public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
        {
            foreach (var t in new[] { "tbl_event", "tbl_sync_quarantine", "tbl_sync_position", "tbl_sync_push_position",
                         "tbl_restore_event_state", "tbl_restore_replay_shield", "tbl_state_anchor" })
                await ctx.Main.ExecuteAsync($"DELETE FROM {t}");
            return new(Name, new Dictionary<string, long>(), []);
        }

        public Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx) =>
            Task.FromResult<IReadOnlyList<RekeyProblem>>(verifyFails ? [new("tbl_event", "*", "an injected verify failure")] : []);
    }

    private sealed class PreflightDouble(bool block = false) : IRekeyPreflight
    {
        public Task<RekeyPreflightReport> RunAsync(string sourceDir, SqliteConnection liveMain, SqliteConnection? liveChat, RekeyKeys keys, CancellationToken ct) =>
            Task.FromResult(new RekeyPreflightReport(block ? [new("tbl_article_body", "x", "an unopenable key")] : [], [], 0));
    }

    private Task<RekeyOutcome> RunAsync(IRekeyStep? chat = null, IRekeyStep? peers = null, IRekeyStep? events = null, bool block = false,
        Action<string>? fault = null, string password = Password) =>
        RekeyRunner.RunAsync(new RekeyOptions
        {
            DataDir = _d,
            OwnerPassword = password,
            Steps = [new KeyMaterialDouble(), new RowResealStep(), chat ?? new ChatDouble(), new DerivedDataClearStep(),
                peers ?? new PeerRevokeDouble(), events ?? new EventLogResetDouble()],
            Preflight = new PreflightDouble(block),
            ChatTables = ChatTablesAllCleared(),
            Fault = fault,
        });

    /// <summary>R2 fills RekeyTables.Chat; until then the tests account for chat.db's tables themselves.</summary>
    private IReadOnlyDictionary<string, TableFate>? ChatTablesAllCleared()
    {
        var chat = Path.Combine(_d, RekeyRunner.ChatDb);
        if (!File.Exists(chat)) return null;
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = chat, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        c.Execute("PRAGMA locking_mode = EXCLUSIVE"); // as the verb opens it: no -wal/-shm files appear in D
        return c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToDictionary(n => n, _ => TableFate.Cleared, StringComparer.OrdinalIgnoreCase);
    }

    private void AssertOldVaultInUse(RekeyOutcome outcome, RekeyExit exit)
    {
        outcome.Exit.Should().Be(exit, outcome.Message);
        Tree(_d).Should().BeEquivalentTo(_tree, "D is byte-identical and still the vault");
        Directory.Exists(RekeySwapJournal.NewDirFor(_d)).Should().BeFalse("no rekey-new is left");
        File.Exists(RekeySwapJournal.LockPathFor(_d)).Should().BeFalse("no lock is left to stop the next start");
        File.Exists(RekeySwapJournal.PathFor(_d)).Should().BeFalse();
    }

    // ------------------------------------------------------------ tests

    [Fact]
    public async Task ARun_ReKeysTheCopy_SwapsItIn_AndLeavesTheOldVaultUntouched()
    {
        var outcome = await RunAsync();

        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);
        var journal = RekeySwapJournal.Read(_d)!;
        journal.Phase.Should().Be(RekeySwapJournal.Swapped);
        Tree(journal.Old).Should().BeEquivalentTo(_tree, "the old vault is never written");
        var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_d, RekeyRunner.ReportFile))).RootElement;
        report.GetProperty("result").GetString().Should().Be("done");
        report.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Equal(RekeyRunner.RequiredSteps);
        report.GetProperty("oldVault").GetString().Should().Be(journal.Old);
        RekeyLock.StartRefusal(RekeySwapResolver.Resolve(_d)).Should().BeNull("the verb has ended; the first start may run");

        // The owner's password opens the new vault to D_c, and the article reads back.
        using var db = new SqliteConnection($"Data Source={Path.Combine(_d, RekeyRunner.MainDb)};Pooling=False");
        db.Open();
        var slot = db.QuerySingle<(byte[] W, byte[] Iv, byte[] Salt, long Mem, long It, long Par)>(
            "SELECT encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot");
        var dc = MasterKeyManager.UnwrapMasterDek(slot.W, slot.Iv, KeyDerivation.DeriveKek(Password, slot.Salt, (int)slot.Mem, (int)slot.It, (int)slot.Par));
        MasterKeyManager.VerifySentinel(db.ExecuteScalar<byte[]>("SELECT sentinel_value FROM tbl_node_identity"), dc).Should().BeTrue();
        var b = db.QuerySingle<(byte[] W, byte[] DekIv, byte[] Iv, byte[] Ct)>(
            "SELECT b.encrypted_dek, b.dek_iv, b.iv, bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @a COLLATE NOCASE",
            new { a = _article.ToString() });
        EnvelopeFraming.Article.DecryptBody(_article, b.W, EnvelopeFraming.Article.UnwrapDek(_article, b.W, b.DekIv, dc), b.Ct, b.Iv).Should().Equal(_bodyPlain);
        db.ExecuteScalar<long>("PRAGMA freelist_count").Should().Be(0, "the copy was scrubbed");
    }

    [Fact]
    public async Task APreflightBlock_LeavesDByteIdentical_AndCreatesNothing()
    {
        AssertOldVaultInUse(await RunAsync(block: true), RekeyExit.PreflightRefused);
    }

    [Fact]
    public async Task AWrongPassword_IsRefusedLikeAPreflightBlock()
    {
        AssertOldVaultInUse(await RunAsync(password: "not the owner"), RekeyExit.PreflightRefused);
    }

    [Theory]
    [InlineData(RekeyRunner.FaultAfterCopy)]
    [InlineData(RekeyRunner.FaultAfterSteps)]
    [InlineData(RekeyRunner.FaultAfterVerify)]
    [InlineData(RekeyRunner.FaultAfterScrub)]
    [InlineData(RekeySwap.FaultCarryOver)]
    public async Task AFaultBeforeTheSwap_LeavesTheOldVaultInUse(string point)
    {
        var outcome = await RunAsync(fault: p => { if (p == point) throw new IOException("power cut at " + p); });

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
    }

    [Fact]
    public async Task AFailingStep_LeavesTheOldVaultInUse_AndTheNextRunDiscardsWhatACrashLeft()
    {
        AssertOldVaultInUse(await RunAsync(chat: new ChatDouble(fail: true)), RekeyExit.FailedBeforeSwap);

        // A crash that skipped the clean-up leaves a copy and a lock file nobody holds.
        Directory.CreateDirectory(RekeySwapJournal.NewDirFor(_d));
        await File.WriteAllTextAsync(Path.Combine(RekeySwapJournal.NewDirFor(_d), "leftover.txt"), "from a dead run");
        await File.WriteAllTextAsync(RekeySwapJournal.LockPathFor(_d), "");

        var outcome = await RunAsync();

        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);
        File.Exists(Path.Combine(_d, "leftover.txt")).Should().BeFalse("the dead run's copy was discarded");
    }

    [Fact]
    public async Task AFailingVerify_LeavesTheOldVaultInUse()
    {
        AssertOldVaultInUse(await RunAsync(events: new EventLogResetDouble(verifyFails: true)), RekeyExit.FailedBeforeSwap);
    }

    /// <summary>One body put back under its old wrapper after the re-seal: the D1 check refuses the copy.</summary>
    [Fact]
    public async Task TheD1Check_RefusesACopyWithOneRowLeftUnderTheOldKey()
    {
        var outcome = await RunAsync(peers: new PeerRevokeDouble(async ctx =>
        {
            using var src = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(ctx.SourceDir, RekeyRunner.MainDb), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            src.Open();
            var old = src.QuerySingle<(byte[] W, byte[] Iv)>("SELECT encrypted_dek, dek_iv FROM tbl_article_body WHERE article_id = @a COLLATE NOCASE",
                new { a = _article.ToString() });
            await ctx.Main.ExecuteAsync("UPDATE tbl_article_body SET encrypted_dek = @W, dek_iv = @Iv WHERE article_id = @a COLLATE NOCASE",
                new { old.W, old.Iv, a = _article.ToString() });
        }));

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("(D1)");
    }

    /// <summary>An old ciphertext copied into a plaintext column: only the byte search can see it, and it does.</summary>
    [Fact]
    public async Task TheD1ByteSearch_RefusesACopyHoldingAnOldCiphertext()
    {
        var outcome = await RunAsync(peers: new PeerRevokeDouble(async ctx =>
        {
            using var src = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(ctx.SourceDir, RekeyRunner.MainDb), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            src.Open();
            var oldBlob = src.ExecuteScalar<byte[]>("SELECT data FROM tbl_blob LIMIT 1");
            await ctx.Main.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES ('smuggled', @oldBlob, 'now')", new { oldBlob });
        }));

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("old ciphertext bytes");
    }
}
