using System.Security.Cryptography;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Rekey;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>bmb rekey</c> on a sealed vault (docs/design/step-b-spec.md 13.2, red test 15.1.12): the copy is made under a NEW database key, the
/// steps and the scrub run keyed, the new vault gets a new boot store under the new master key only, and nothing of the old boot store (a
/// door, a wrapper) or of the old page keys opens the new files. The run is the whole plan as the verb runs it, on a real node's data
/// directory sealed with the page codec. The boot store and the database key service are doubles (tracks 2's <c>WriteNewBootStoreAsync</c>
/// and <c>IDatabaseKeyService</c> are not on this branch): the double keeps real files and real AES-GCM wrappers, so "which door opens
/// which file" is decided by bytes, not by a flag.
/// </summary>
public sealed class RekeySealedVerbTests : IAsyncLifetime, IClassFixture<RekeySealedVerbTests.NodeTemplate>
{
    private const string Password = NodeTemplate.Password;
    private readonly NodeTemplate _template;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-sealed-verb-" + Guid.NewGuid().ToString("N"));
    private string _d = "";
    private string _plain = "";
    private byte[] _oldDbk = [];
    private InMemoryVaultKeySource _sourceKeys = new();
    private Dictionary<string, string> _tree = [];
    private readonly FakeMirror _mirror = new();

    public RekeySealedVerbTests(NodeTemplate template) => _template = template;

    private string _canary => _template.Canary;
    private byte[] _oldDek => _template.OldDek;
    private Guid _article => _template.Article;
    private byte[] _bodyPlain => _template.BodyPlain;

    /// <summary>A real node's stopped data directory, built once for the class (the node's start and Argon2 take most of a test's time).</summary>
    public sealed class NodeTemplate : IAsyncLifetime
    {
        public const string Password = "rekeySealedPw1";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-sealed-verb-template-" + Guid.NewGuid().ToString("N"));
        public string PlainDir => Path.Combine(Root, "plain");
        public string Canary { get; } = MetadataCanaries.Ascii("title");
        public byte[] OldDek { get; private set; } = [];
        public Guid Article { get; private set; }
        public byte[] BodyPlain { get; private set; } = [];

        public async Task InitializeAsync()
        {
            using var factory = new BmbWebApplicationFactory();
            await factory.InitializeNodeAsync("Owner", Password);
            var session = factory.Services.GetRequiredService<SessionService>();
            (await session.UnlockAsync(Password)).Should().BeTrue();
            using (var scope = factory.Services.CreateScope())
            {
                var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
                Article = (await articles.CreateAsync("Kept " + Canary, "/Notes", [], "body v1")).Id;
                await articles.UpdateAsync(Article, plaintext: "body v2");
                await scope.ServiceProvider.GetRequiredService<CommentService>().CreateAsync(Article, "a comment");
            }
            using (var conn = factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            {
                await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
                var b = await conn.QuerySingleAsync<(byte[] W, byte[] DekIv, byte[] Iv, byte[] Ct)>(
                    "SELECT b.encrypted_dek, b.dek_iv, b.iv, bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @a COLLATE NOCASE",
                    new { a = Article.ToString() });
                OldDek = (byte[])session.GetMasterDek().Clone();
                BodyPlain = EnvelopeFraming.Article.DecryptBody(Article, b.W, EnvelopeFraming.Article.UnwrapDek(Article, b.W, b.DekIv, OldDek), b.Ct, b.Iv);
            }
            var chat = Path.Combine(factory.DataPath, RekeyRunner.ChatDb);
            if (File.Exists(chat))
            {
                using var c = new SqliteConnection($"Data Source={chat};Pooling=False");
                c.Open();
                c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            SqliteConnection.ClearAllPools();
            CopyTree(factory.DataPath, PlainDir, skipDatabases: false);
            await File.WriteAllTextAsync(Path.Combine(PlainDir, ".internal-key"), "internal");
        }

        public Task DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        // xUnit does not call DisposeAsync when InitializeAsync throws: the folder would stay.
        try { await BuildAsync(); }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task BuildAsync()
    {
        // The node, stopped: one copy stays plain (for the tests that give a plain vault sealed options), the other is sealed with the
        // page codec under a database key of generation 1.
        _plain = Path.Combine(_root, "plain");
        CopyTree(_template.PlainDir, _plain, skipDatabases: false);
        _d = Path.Combine(_root, "vault");
        CopyTree(_plain, _d, skipDatabases: true);

        _oldDbk = RandomNumberGenerator.GetBytes(32);
        _sourceKeys = new InMemoryVaultKeySource(FakeKeyService.PageKey(_oldDbk, DbPurposes.Vault), FakeKeyService.PageKey(_oldDbk, DbPurposes.Chat));
        await SealAsync(DbPurposes.Vault, RekeyRunner.MainDb);
        if (File.Exists(Path.Combine(_plain, RekeyRunner.ChatDb))) await SealAsync(DbPurposes.Chat, RekeyRunner.ChatDb);
        Directory.CreateDirectory(Path.Combine(_d, "certs"));
        await File.WriteAllTextAsync(Path.Combine(_d, "certs", "ca.pem"), "ca");
        using (var oldVault = new DbConnectionFactory(Path.Combine(_d, RekeyRunner.MainDb), _sourceKeys))
            await _mirror.WriteNewBootStoreAsync(Path.Combine(_d, VaultStoreFiles.Boot), oldVault, _oldDbk, 1, [_oldDek]);
        SqliteConnection.ClearAllPools();
        _mirror.Calls.Clear();
        _tree = Tree(_d);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private async Task SealAsync(string purpose, string file)
    {
        var key = _sourceKeys.GetPageKey(purpose);
        var source = Path.Combine(_plain, file);
        await using var image = File.OpenRead(source);
        await PlainImageImporter.ToSealedFileAsync(image, image.Length, Path.Combine(_d, file), key, memoryCap: 256L << 20);
    }

    private static void CopyTree(string from, string to, bool skipDatabases)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(f);
            // The instance lock of a running node is never copied (on Linux it is an exclusive flock that File.Copy cannot read).
            if (f.EndsWith("-shm") || f.EndsWith("-wal") || name == "node.lock" || name == InstanceGuard.FileName) continue;
            if (skipDatabases && name is RekeyRunner.MainDb or RekeyRunner.ChatDb) continue;
            File.Copy(f, Path.Combine(to, name));
        }
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), skipDatabases: false);
    }

    private static Dictionary<string, string> Tree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    // ------------------------------------------------------------ the doubles of tracks 2 and 3

    /// <summary>
    /// Track 2's <c>IDatabaseKeyService</c> over a plain SQLite "boot store" file: the wrappers are AES-GCM of the database key under a
    /// master DEK, found by the DEK's fingerprint. <see cref="TryUnwrapAsync"/> reads the store the service is bound to (the OLD one).
    /// </summary>
    private sealed class FakeKeyService(string bootPath, Func<byte[]>? create = null, bool unwrapsAnything = false) : IDatabaseKeyService
    {
        public byte[] Create() => create?.Invoke() ?? RandomNumberGenerator.GetBytes(32);

        public byte[] DerivePageKey(ReadOnlySpan<byte> databaseKey, string purpose) => PageKey(databaseKey, purpose);

        public static byte[] PageKey(ReadOnlySpan<byte> databaseKey, string purpose) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, databaseKey.ToArray(), 32, info: System.Text.Encoding.ASCII.GetBytes("fake-page-key:" + purpose));

        public Task<byte[]?> TryUnwrapAsync(byte[] masterDek, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(unwrapsAnything ? create?.Invoke() ?? RandomNumberGenerator.GetBytes(32) : FakeMirror.UnwrapDbk(bootPath, masterDek));
    }

    /// <summary>
    /// Track 2's <c>WriteNewBootStoreAsync</c>: a plain SQLite file with the boot store's application id, the doors (key slots) read from the
    /// vault it is handed, and the database key wrapped under every master DEK it is given.
    /// </summary>
    private sealed class FakeMirror : IUnlockMaterialMirror
    {
        public List<(string Path, int Generation, byte[][] MasterDeks, byte[] Dbk)> Calls { get; } = [];

        public Task RewriteAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
            IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default)
        {
            Calls.Add((bootPath, dbkGeneration, [.. masterDeks.Select(k => (byte[])k.Clone())], (byte[])databaseKey.Clone()));
            using (var boot = new SqliteConnection($"Data Source={bootPath};Pooling=False"))
            {
                boot.Open();
                boot.Execute($"PRAGMA application_id = {VaultStoreFiles.BootApplicationId}");
                boot.Execute("CREATE TABLE door (slot_id INTEGER PRIMARY KEY, encrypted_master_dek BLOB, iv BLOB, salt BLOB, argon_memory INTEGER, argon_iterations INTEGER, argon_parallelism INTEGER)");
                boot.Execute("CREATE TABLE wrapper (fingerprint BLOB PRIMARY KEY, wrapped BLOB, generation INTEGER)");
                using (var conn = vault.CreateConnection())
                    foreach (var s in await conn.QueryAsync<(long SlotId, byte[] W, byte[] Iv, byte[] Salt, long Mem, long It, long Par)>(
                                 "SELECT slot_id, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM tbl_key_slot WHERE salt IS NOT NULL"))
                        boot.Execute("INSERT INTO door VALUES (@slot, @w, @iv, @salt, @mem, @it, @par)",
                            new { slot = s.SlotId, w = s.W, iv = s.Iv, salt = s.Salt, mem = s.Mem, it = s.It, par = s.Par });
                foreach (var dek in masterDeks)
                {
                    var nonce = RandomNumberGenerator.GetBytes(12);
                    var tag = new byte[16];
                    var cipher = new byte[databaseKey.Length];
                    using (var gcm = new AesGcm(dek, 16)) gcm.Encrypt(nonce, databaseKey, cipher, tag, "fake-dbk-wrapper"u8);
                    boot.Execute("INSERT INTO wrapper VALUES (@f, @w, @g)", new { f = Fingerprint(dek), w = nonce.Concat(tag).Concat(cipher).ToArray(), g = dbkGeneration });
                }
            }
            SqliteConnection.ClearAllPools();
        }

        public static byte[] Fingerprint(byte[] dek) => SHA256.HashData(dek)[..16];

        public static byte[]? UnwrapDbk(string bootPath, byte[] masterDek)
        {
            using var boot = new SqliteConnection($"Data Source={bootPath};Pooling=False;Mode=ReadOnly");
            boot.Open();
            var wrapped = boot.ExecuteScalar<byte[]?>("SELECT wrapped FROM wrapper WHERE fingerprint = @f", new { f = Fingerprint(masterDek) });
            if (wrapped is null) return null;
            var plain = new byte[wrapped.Length - 28];
            try
            {
                using var gcm = new AesGcm(masterDek, 16);
                gcm.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain, "fake-dbk-wrapper"u8);
                return plain;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>The unlock a person performs: the password opens a door to a master DEK, the DEK's wrapper opens the database key.</summary>
        public static (byte[] Dek, byte[] Dbk)? Unlock(string bootPath, string password)
        {
            using var boot = new SqliteConnection($"Data Source={bootPath};Pooling=False;Mode=ReadOnly");
            boot.Open();
            foreach (var s in boot.Query<(byte[] W, byte[] Iv, byte[] Salt, long Mem, long It, long Par)>(
                         "SELECT encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism FROM door"))
            {
                byte[] dek;
                try { dek = MasterKeyManager.UnwrapMasterDek(s.W, s.Iv, KeyDerivation.DeriveKek(password, s.Salt, (int)s.Mem, (int)s.It, (int)s.Par)); }
                catch (CryptographicException) { continue; }
                if (UnwrapDbk(bootPath, dek) is { } dbk) return (dek, dbk);
            }
            return null;
        }
    }

    /// <summary>The pre-flight of the plan, recording that the runner gave it the source's key source and took it back.</summary>
    private sealed class RecordingPreflight : IRekeyPreflight, IRekeySealedSourceConsumer
    {
        private readonly IRekeyPreflight _inner = RekeyPlan.Preflight!.Invoke();
        public List<bool> Sets { get; } = [];
        public void SetSealedSource(IVaultKeySource? source)
        {
            Sets.Add(source is not null);
            (_inner as IRekeySealedSourceConsumer)?.SetSealedSource(source);
        }

        public Task<RekeyPreflightReport> RunAsync(string sourceDir, SqliteConnection liveMain, SqliteConnection? liveChat, RekeyKeys keys, CancellationToken ct) =>
            _inner.RunAsync(sourceDir, liveMain, liveChat, keys, ct);
    }

    // ------------------------------------------------------------ running

    private RekeySealedOptions SealedOptions(IDatabaseKeyService? keyService = null, IVaultKeySource? source = null) => new()
    {
        SourceKeys = source ?? _sourceKeys,
        SourceDbkGeneration = 1,
        KeyService = keyService ?? new FakeKeyService(Path.Combine(_d, VaultStoreFiles.Boot)),
        Mirror = _mirror,
    };

    private Task<RekeyOutcome> RunAsync(string? dir = null, RekeySealedOptions? sealedOptions = null, string password = Password,
        Action<string>? fault = null, IRekeyPreflight? preflight = null, bool withSealed = true) =>
        RekeyRunner.RunAsync(new RekeyOptions
        {
            DataDir = dir ?? _d,
            OwnerPassword = password,
            Steps = RekeyPlan.Steps.Select(f => f()).ToList(),
            Preflight = preflight ?? RekeyPlan.Preflight?.Invoke(),
            Sealed = withSealed ? sealedOptions ?? SealedOptions() : null,
            Fault = fault,
        });

    private void AssertOldVaultInUse(RekeyOutcome outcome, RekeyExit exit)
    {
        outcome.Exit.Should().Be(exit, outcome.Message);
        Tree(_d).Should().BeEquivalentTo(_tree, "D (sealed files, boot store and all) is byte-identical and still the vault");
        Directory.Exists(RekeySwapJournal.NewDirFor(_d)).Should().BeFalse("no rekey-new is left");
        File.Exists(RekeySwapJournal.LockPathFor(_d)).Should().BeFalse("no lock is left to stop the next start");
        File.Exists(RekeySwapJournal.PathFor(_d)).Should().BeFalse();
    }

    private static bool OpensWith(string file, byte[] key)
    {
        try
        {
            using var c = SealedSqlite.OpenKeyed(file, key, readOnly: true, busyTimeoutSeconds: 1);
            return true;
        }
        catch (VaultKeyMismatchException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ 15.1.12

    [Fact]
    public async Task A_sealed_vault_is_re_keyed_the_new_vault_opens_with_the_password_only_and_nothing_of_the_old_boot_store_opens_it()
    {
        var preflight = new RecordingPreflight();

        var outcome = await RunAsync(preflight: preflight);

        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);
        preflight.Sets.Should().Equal(true, false);
        var journal = RekeySwapJournal.Read(_d)!;
        journal.Phase.Should().Be(RekeySwapJournal.Swapped);
        Tree(journal.Old).Should().BeEquivalentTo(_tree, "the parked old vault keeps its own files and its own boot store, unchanged");

        // What the boot store writer was handed: generation + 1, the new master key alone.
        var call = _mirror.Calls.Should().ContainSingle().Subject;
        call.Generation.Should().Be(2);
        call.MasterDeks.Should().ContainSingle().Which.Should().NotEqual(_oldDek);
        call.Dbk.Should().NotEqual(_oldDbk);
        Path.GetFileName(call.Path).Should().Be(VaultStoreFiles.Boot);

        // The new directory is a sealed pair: no SQLite header in either file, and the engine's own diagnosis agrees.
        VaultStoreFiles.Diagnose(_d).Mode.Should().Be(VaultStoreMode.Sealed);
        VaultStoreFiles.HasSqliteMagic(Path.Combine(_d, RekeyRunner.MainDb)).Should().BeFalse();
        var newBoot = Path.Combine(_d, VaultStoreFiles.Boot);
        VaultStoreFiles.ReadApplicationId(newBoot).Should().Be(VaultStoreFiles.BootApplicationId);

        // The owner's password opens the new vault: door -> master DEK -> wrapper -> database key -> page key -> the file.
        var unlocked = FakeMirror.Unlock(newBoot, Password);
        unlocked.Should().NotBeNull("the password opens a door of the new boot store, and the new master key's wrapper opens the database key");
        var (dek, dbk) = unlocked!.Value;
        dek.Should().Equal(call.MasterDeks[0]);
        dbk.Should().Equal(call.Dbk);
        using (var vault = SealedSqlite.OpenKeyed(Path.Combine(_d, RekeyRunner.MainDb), FakeKeyService.PageKey(dbk, DbPurposes.Vault)))
        {
            MasterKeyManager.VerifySentinel(vault.ExecuteScalar<byte[]>("SELECT sentinel_value FROM tbl_node_identity")!, dek).Should().BeTrue();
            var b = vault.QuerySingle<(byte[] W, byte[] DekIv, byte[] Iv, byte[] Ct)>(
                "SELECT b.encrypted_dek, b.dek_iv, b.iv, bl.data FROM tbl_article_body b JOIN tbl_blob bl ON bl.hash = b.ciphertext_hash WHERE b.article_id = @a COLLATE NOCASE",
                new { a = _article.ToString() });
            EnvelopeFraming.Article.DecryptBody(_article, b.W, EnvelopeFraming.Article.UnwrapDek(_article, b.W, b.DekIv, dek), b.Ct, b.Iv)
                .Should().Equal(_bodyPlain);
            vault.ExecuteScalar<long>("PRAGMA freelist_count").Should().Be(0, "the copy was scrubbed");
            vault.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
        }

        // Nothing of the old vault opens it: the old page keys, the old master key (its wrapper is not in the new store), and the old
        // boot store's door and wrapper chain (the password reaches the old master key there, and the old database key from it).
        foreach (var (file, purpose) in new[] { (RekeyRunner.MainDb, DbPurposes.Vault), (RekeyRunner.ChatDb, DbPurposes.Chat) })
        {
            var path = Path.Combine(_d, file);
            if (!File.Exists(path)) continue;
            OpensWith(path, _sourceKeys.GetPageKey(purpose)).Should().BeFalse($"{file}: the old vault's page key");
            var oldChain = FakeMirror.Unlock(Path.Combine(journal.Old, VaultStoreFiles.Boot), Password)!.Value;
            OpensWith(path, FakeKeyService.PageKey(oldChain.Dbk, purpose)).Should().BeFalse($"{file}: the old boot store's door and wrapper");
            OpensWith(Path.Combine(journal.Old, file), FakeKeyService.PageKey(oldChain.Dbk, purpose)).Should().BeTrue("the control: the old chain opens the old file");
        }
        FakeMirror.UnwrapDbk(newBoot, _oldDek).Should().BeNull("no wrapper of the new boot store is under the old master key");
        FakeMirror.UnwrapDbk(Path.Combine(journal.Old, VaultStoreFiles.Boot), dek).Should().BeNull("no wrapper of the old boot store is under the new master key");

        // The canary of the vault's metadata is not in any byte of the new directory.
        MetadataCanaries.ScanDirectory(_d, [_canary]).Should().BeEmpty();

        var report = RekeyReport.TryRead(_d)!;
        report.Result.Should().Be(RekeyReport.Done);
        report.Steps.Should().Contain(s => s.Name == RekeyRunner.SealedVaultName && s.Counts["dbk_generation"] == 2);
    }

    [Fact]
    public async Task The_source_key_source_is_given_to_the_steps_that_read_the_live_file_and_taken_back_even_when_the_run_fails()
    {
        var preflight = new RecordingPreflight();

        var outcome = await RunAsync(preflight: preflight, fault: p => { if (p == RekeyRunner.FaultAfterCopy) throw new IOException("power cut"); });

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        preflight.Sets.Should().Equal(true, false);
    }

    [Fact]
    public async Task A_wrapper_of_the_old_boot_store_that_opens_the_new_vault_stops_the_run_and_leaves_the_old_vault_in_use()
    {
        // The mutation: a key service that hands out the OLD database key again, so the old store's wrapper opens the new files.
        var reused = new FakeKeyService(Path.Combine(_d, VaultStoreFiles.Boot), create: () => (byte[])_oldDbk.Clone());

        var outcome = await RunAsync(sealedOptions: SealedOptions(reused));

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("opens with the old vault's page key");
    }

    [Fact]
    public async Task An_old_boot_store_with_a_wrapper_under_the_new_master_key_stops_the_run()
    {
        var lying = new FakeKeyService(Path.Combine(_d, VaultStoreFiles.Boot), unwrapsAnything: true);

        var outcome = await RunAsync(sealedOptions: SealedOptions(lying));

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("old boot store");
    }

    [Fact]
    public async Task A_sealed_vault_given_no_keys_is_refused_with_nothing_created()
    {
        var outcome = await RunAsync(withSealed: false);

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        outcome.Message.Should().Contain("encrypted vault");
    }

    [Fact]
    public async Task Sealed_options_on_a_plain_vault_are_refused_and_the_plain_vault_is_left_alone()
    {
        var before = Tree(_plain);

        var outcome = await RunAsync(dir: _plain);

        outcome.Exit.Should().Be(RekeyExit.FailedBeforeSwap, outcome.Message);
        outcome.Message.Should().Contain("not an encrypted vault");
        Tree(_plain).Should().BeEquivalentTo(before);
        Directory.Exists(RekeySwapJournal.NewDirFor(_plain)).Should().BeFalse();
    }

    [Fact]
    public async Task A_closed_source_fails_before_anything_is_created()
    {
        var closed = new InMemoryVaultKeySource();
        closed.Close();

        var outcome = await RunAsync(sealedOptions: SealedOptions(source: closed));

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
    }

    [Fact]
    public async Task The_wrong_page_key_for_the_source_fails_before_anything_is_created_and_names_no_key()
    {
        var wrong = new InMemoryVaultKeySource();

        var outcome = await RunAsync(sealedOptions: SealedOptions(source: wrong));

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
        foreach (var key in new[] { wrong.GetPageKey(DbPurposes.Vault), _sourceKeys.GetPageKey(DbPurposes.Vault) })
            outcome.Message.Should().NotContain(Convert.ToHexStringLower(key)).And.NotContain(Convert.ToHexString(key));
    }

    [Fact]
    public async Task A_wrong_password_is_refused_like_on_a_plain_vault()
    {
        AssertOldVaultInUse(await RunAsync(password: "not the owner"), RekeyExit.PreflightRefused);
    }

    [Theory]
    [InlineData(RekeyRunner.FaultAfterCopy)]
    [InlineData(RekeyRunner.FaultAfterSteps)]
    [InlineData(RekeyRunner.FaultAfterVerify)]
    [InlineData(RekeyRunner.FaultAfterScrub)]
    [InlineData(RekeyRunner.FaultAfterBootStore)]
    [InlineData(RekeySwap.FaultCarryOver)]
    public async Task A_fault_before_the_swap_leaves_the_old_sealed_vault_in_use(string point)
    {
        var outcome = await RunAsync(fault: p => { if (p == point) throw new IOException("power cut at " + p); });

        AssertOldVaultInUse(outcome, RekeyExit.FailedBeforeSwap);
    }

    [Fact]
    public async Task No_plain_database_page_exists_in_the_new_directory_while_the_run_goes_on()
    {
        var newDir = RekeySwapJournal.NewDirFor(_d);
        var plainFiles = new System.Collections.Concurrent.ConcurrentBag<string>();
        var canaryHits = new System.Collections.Concurrent.ConcurrentBag<string>();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var stop = false;
        using var started = new ManualResetEventSlim();
        var poller = new Thread(() =>
        {
            started.Set();
            var magic = "SQLite format 3\0"u8.ToArray();
            while (!Volatile.Read(ref stop))
            {
                if (Directory.Exists(newDir))
                    foreach (var file in Directory.GetFiles(newDir))
                    {
                        var name = Path.GetFileName(file);
                        seen.TryAdd(name, 0);
                        try
                        {
                            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var head = new byte[16];
                            if (s.ReadAtLeast(head, 16, throwOnEndOfStream: false) == 16 && head.AsSpan().SequenceEqual(magic) && name != VaultStoreFiles.Boot)
                                plainFiles.Add(name);
                            s.Position = 0;
                            var all = new byte[s.Length];
                            s.ReadExactly(all);
                            foreach (var hit in MetadataCanaries.Scan(all, [_canary], file)) canaryHits.Add(name + ":" + hit.Encoding);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        poller.Start();
        started.Wait();

        var outcome = await RunAsync();
        Thread.Sleep(50);
        Volatile.Write(ref stop, true);
        poller.Join();

        outcome.Exit.Should().Be(RekeyExit.Done, outcome.Message);
        seen.Keys.Should().Contain(RekeyRunner.MainDb, "the poller did watch the copy");
        plainFiles.Should().BeEmpty("only the boot store is a plain SQLite file; the copy and the scrub were sealed at every moment");
        canaryHits.Should().BeEmpty();
    }
}
