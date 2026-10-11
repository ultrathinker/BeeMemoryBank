using System.Data;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// The keyed DbConnectionFactory (docs/design/step-b-spec.md 7.1-7.3, red tests 15.1.1-15.1.4). A sealed file is a SQLCipher 4 file under
/// a raw 256-bit page key that an <see cref="IVaultKeySource"/> holds while the vault is open; a closed key source means
/// <see cref="VaultSealedException"/> and never an unkeyed open.
/// </summary>
public sealed class KeyedFactoryTests : IDisposable
{
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_keyed_" + Guid.NewGuid().ToString("N"));

    public KeyedFactoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, "beememorybank.db");

    private static void Run(IDbConnection c, string sql, object? args = null) => c.Execute(sql, args);

    private static bool HasMagic(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[16];
        return fs.Read(head) == 16 && head.AsSpan().SequenceEqual(SqliteMagic);
    }

    private static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // ---------------------------------------------------------------- 15.1.1: the keyed factory

    [Fact]
    public void A_sealed_factory_writes_and_reopens_pooled_and_its_files_hold_no_canary_and_no_sqlite_magic()
    {
        var canary = MetadataCanaries.Ascii("title");
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);

        using (var c = factory.CreateConnection())
        {
            Run(c, "CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT NOT NULL)");
            Run(c, "INSERT INTO tbl_article (title) VALUES (@canary)", new { canary });
        }
        using (var again = factory.CreateConnection())
            again.QuerySingle<string>("SELECT title FROM tbl_article").Should().Be(canary, "the pooled connection is keyed too");

        // Both connections are still pooled: the WAL has not been checkpointed, so the canary is in -wal now (and -shm exists).
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        files.Should().Contain(["beememorybank.db", "beememorybank.db-wal", "beememorybank.db-shm"]);
        new FileInfo(DbPath + "-wal").Length.Should().BeGreaterThan(0);
        foreach (var file in Directory.GetFiles(_dir))
            MetadataCanaries.ScanFile(file, [canary]).Should().BeEmpty($"{Path.GetFileName(file)} is encrypted");
        HasMagic(DbPath).Should().BeFalse("a sealed file has no SQLite header");
        var length = new FileInfo(DbPath).Length;
        length.Should().BeGreaterThan(0);
        (length % 4096).Should().Be(0, "a sealed file is whole 4096-byte pages");
    }

    [Fact]
    public void The_same_data_on_a_plain_factory_does_hold_the_canary_which_proves_the_scan_can_see_it()
    {
        var canary = MetadataCanaries.Ascii("title");
        using var factory = new DbConnectionFactory(_dir);
        using (var c = factory.CreateConnection())
        {
            Run(c, "CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT NOT NULL)");
            Run(c, "INSERT INTO tbl_article (title) VALUES (@canary)", new { canary });
        }

        Directory.GetFiles(_dir).SelectMany(f => MetadataCanaries.ScanFile(f, [canary])).Should().NotBeEmpty();
        HasMagic(DbPath).Should().BeTrue();
    }

    [Fact]
    public void A_wrong_page_key_fails_at_open_with_VaultKeyMismatchException()
    {
        var right = new InMemoryVaultKeySource();
        using (var factory = new DbConnectionFactory(_dir, right))
        using (var c = factory.CreateConnection())
            Run(c, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1)");
        SqliteConnection.ClearAllPools();
        var before = Sha256(DbPath);

        var wrong = new InMemoryVaultKeySource();
        using var other = new DbConnectionFactory(_dir, wrong);
        var act = () => other.CreateConnection();

        act.Should().Throw<VaultKeyMismatchException>();
        other.OpenConnections.Should().Be(0, "a connection that failed to open is not left counted");
        SqliteConnection.ClearAllPools();
        Sha256(DbPath).Should().Be(before, "a failed open writes nothing");
    }

    [Fact]
    public void A_closed_key_source_throws_VaultSealedException_and_opens_no_file()
    {
        var keys = new InMemoryVaultKeySource();
        keys.Close();
        using var factory = new DbConnectionFactory(_dir, keys);

        var act = () => factory.CreateConnection();

        act.Should().Throw<VaultSealedException>();
        File.Exists(DbPath).Should().BeFalse("opening the file would have created it");
        factory.OpenConnections.Should().Be(0);
        Directory.GetFileSystemEntries(_dir).Should().BeEmpty();
    }

    [Fact]
    public void A_closed_key_source_over_an_existing_file_holds_no_handle_on_it()
    {
        var keys = new InMemoryVaultKeySource();
        using (var seed = new DbConnectionFactory(_dir, keys))
        using (var c = seed.CreateConnection())
            Run(c, "CREATE TABLE t (x INTEGER)");
        SqliteConnection.ClearAllPools();
        keys.Close();
        using var factory = new DbConnectionFactory(_dir, keys);

        var act = () => factory.CreateConnection();
        act.Should().Throw<VaultSealedException>();

        if (OperatingSystem.IsWindows())
        {
            // Windows enforces share modes: a handle left on the file by the failed call would refuse this exclusive open.
            using var exclusive = new FileStream(DbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            exclusive.Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void A_broken_store_mode_is_closed_too()
    {
        using var factory = new DbConnectionFactory(_dir, new FixedModeKeySource(VaultStoreMode.Broken));

        var act = () => factory.CreateConnection();

        act.Should().Throw<VaultSealedException>();
        File.Exists(DbPath).Should().BeFalse();
    }

    [Fact]
    public void A_plain_mode_key_source_behaves_exactly_like_no_key_source()
    {
        using var factory = new DbConnectionFactory(_dir, new FixedModeKeySource(VaultStoreMode.Plain));
        using (var c = factory.CreateConnection())
        {
            Run(c, "CREATE TABLE t (x INTEGER)");
            c.QuerySingle<int>("PRAGMA temp_store").Should().Be(1);
        }

        HasMagic(DbPath).Should().BeTrue();
        factory.DatabaseId.Should().Be("Data Source=" + DbPath);
    }

    [Fact]
    public void CreateSealedForTests_gives_a_working_keyed_factory_on_a_temp_file()
    {
        var keys = new InMemoryVaultKeySource();
        string path;
        using (var factory = DbConnectionFactory.CreateSealedForTests("keyed_unit", keys))
        {
            path = factory.FilePath;
            using (var c = factory.CreateConnection())
            {
                Run(c, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (7)");
                c.QuerySingle<int>("SELECT x FROM t").Should().Be(7);
            }
            File.Exists(path).Should().BeTrue();
            HasMagic(path).Should().BeFalse();
        }

        File.Exists(path).Should().BeFalse("the temp file goes with the factory, as for CreateInMemory");
    }

    [Fact]
    public async Task Many_threads_get_working_pooled_connections_whichever_ones_are_fresh()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (41)");

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                using var c = factory.CreateConnection();
                c.QuerySingle<int>("SELECT x FROM t").Should().Be(41);
            }
        }));

        await Task.WhenAll(tasks);
        factory.OpenConnections.Should().Be(0);
    }

    // ---------------------------------------------------------------- generations, close, OpenFile

    [Fact]
    public async Task CloseForSealAsync_ends_the_generation_even_while_the_key_source_still_says_open()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");

        (await factory.CloseForSealAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        // The gate clears the keys only after CloseForSealAsync returns (spec 5.4 step 4); nothing may reopen the generation in between.
        keys.IsOpen.Should().BeTrue();
        var act = () => factory.CreateConnection();
        act.Should().Throw<VaultSealedException>();
        var openFile = () => factory.OpenFile(DbPath, readOnly: true);
        openFile.Should().Throw<VaultSealedException>();
    }

    [Fact]
    public async Task A_new_generation_with_a_new_key_opens_the_file_that_was_rekeyed_and_refuses_the_old_key()
    {
        var keys = new InMemoryVaultKeySource();
        var oldKey = keys.GetPageKey(DbPurposes.Vault);
        using var factory = new DbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (5)");
        await factory.CloseForSealAsync(TimeSpan.FromSeconds(5));
        keys.Close();

        // Second generation, same key: works (a closed node unlocked again).
        keys.Open(oldKey, RandomNumberGenerator.GetBytes(32));
        using (var c = factory.CreateConnection()) c.QuerySingle<int>("SELECT x FROM t").Should().Be(5);
        await factory.CloseForSealAsync(TimeSpan.FromSeconds(5));
        keys.Close();

        // Third generation with a different key: the file does not open, and nothing falls back to the old pool.
        keys.Open(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var act = () => factory.CreateConnection();
        act.Should().Throw<VaultKeyMismatchException>();
    }

    [Fact]
    public async Task CloseForSealAsync_waits_for_open_connections_and_reports_a_straggler_after_the_limit()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        var held = factory.CreateConnection();
        Run(held, "CREATE TABLE t (x INTEGER)");

        var clean = await factory.CloseForSealAsync(TimeSpan.FromMilliseconds(300));
        clean.Should().BeFalse("one connection was still in use at the limit");
        held.Dispose();
        SqliteConnection.ClearAllPools();
        factory.OpenConnections.Should().Be(0);

        var again = () => factory.CreateConnection();
        again.Should().Throw<VaultSealedException>();
    }

    [Fact]
    public async Task CloseForSealAsync_inside_a_quiesce_of_the_same_flow_does_not_start_a_second_one()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");

        using (factory.BeginQuiesce())
        {
            await factory.WaitDrainedAsync(TimeSpan.FromSeconds(5));
            // The gate's checkpoint connection (spec 5.4 step 3) is opened inside the quiesce, from the same flow.
            using (var last = factory.CreateConnection()) last.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            (await factory.CloseForSealAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        factory.OpenConnections.Should().Be(0);
    }

    [Fact]
    public void OpenFile_opens_a_keyed_copy_not_pooled_and_counts_it_in_the_drain()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        var canary = MetadataCanaries.Ascii("copy");
        using (var c = factory.CreateConnection())
        {
            Run(c, "CREATE TABLE t (x TEXT); INSERT INTO t VALUES (@canary)", new { canary });
            var copy = Path.Combine(_dir, "staging.db");
            Run(c, $"VACUUM INTO '{copy.Replace("'", "''")}'");
        }
        var staging = Path.Combine(_dir, "staging.db");
        HasMagic(staging).Should().BeFalse("VACUUM INTO on a keyed connection writes a copy under the same key");
        MetadataCanaries.ScanFile(staging, [canary]).Should().BeEmpty();

        var before = factory.OpenConnections;
        using (var file = factory.OpenFile(staging, readOnly: false))
        {
            file.QuerySingle<string>("SELECT x FROM t").Should().Be(canary);
            factory.OpenConnections.Should().Be(before + 1, "a staging connection counts in the drain (spec 5.4)");
        }
        factory.OpenConnections.Should().Be(before);

        using (var ro = factory.OpenFile(staging, readOnly: true))
        {
            ro.QuerySingle<string>("SELECT x FROM t").Should().Be(canary);
            var write = () => ro.Execute("INSERT INTO t VALUES ('x')");
            write.Should().Throw<SqliteException>("a read-only open cannot write");
        }
        // Not pooled: nothing keeps the file open after the dispose, so it can be deleted (Windows would refuse otherwise).
        File.Delete(staging);
        File.Exists(staging).Should().BeFalse();
    }

    [Fact]
    public void OpenFile_on_a_plain_factory_opens_a_plain_file_and_is_not_counted()
    {
        using var factory = new DbConnectionFactory(_dir);
        var other = Path.Combine(_dir, "other.db");
        using (var seed = new SqliteConnection($"Data Source={other};Pooling=False")) { seed.Open(); seed.Execute("CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (3)"); }

        using var file = factory.OpenFile(other, readOnly: true);

        file.QuerySingle<int>("SELECT x FROM t").Should().Be(3);
        factory.OpenConnections.Should().Be(0);
    }

    // ---------------------------------------------------------------- 15.1.2: temp store

    [Fact]
    public void Keyed_connections_keep_temp_data_in_memory_and_plain_ones_on_disk()
    {
        using var keyed = new DbConnectionFactory(_dir, new InMemoryVaultKeySource());
        using (var c = keyed.CreateConnection()) c.QuerySingle<int>("PRAGMA temp_store").Should().Be(2, "2 = MEMORY: SQLite3MC does not encrypt temp files");

        using var plain = new DbConnectionFactory(Path.Combine(_dir, "plain"));
        using (var c = plain.CreateConnection()) c.QuerySingle<int>("PRAGMA temp_store").Should().Be(1, "1 = FILE, as before Step B");
    }

    [Collection("SQLite temp directory")]
    public sealed class TempStore : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_keyed_tmp_" + Guid.NewGuid().ToString("N"));
        private readonly string _tempDir;

        public TempStore()
        {
            Directory.CreateDirectory(_dir);
            _tempDir = Path.Combine(_dir, "sqlite-temp");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string DiagnosticLine = "";

        private static int SortAndCountSpilledFiles(IDbConnection c, string tempDir)
        {
            // SQLite's sorter spills to temp files once its rows no longer fit the page cache; the cache is made tiny to force that.
            c.Execute("PRAGMA temp_store_directory = '" + tempDir.Replace("'", "''") + "'");
            try
            {
                c.Execute("PRAGMA cache_size = -64");
                c.Execute("CREATE TABLE big (id INTEGER PRIMARY KEY, payload TEXT)");
                using (var tx = c.BeginTransaction())
                {
                    for (var i = 0; i < 20000; i++) c.Execute("INSERT INTO big (payload) VALUES (@p)", new { p = Guid.NewGuid().ToString("N") + new string('x', 200) }, tx);
                    tx.Commit();
                }

                // A sorter file lives for a moment: a poll of the folder every millisecond, and a watcher as well.
                var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
                using var watcher = new FileSystemWatcher(tempDir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
                watcher.Created += (_, e) => seen.TryAdd(e.Name ?? "?", 0);
                watcher.Renamed += (_, e) => seen.TryAdd(e.Name ?? "?", 0);
                var stop = false;
                using var started = new ManualResetEventSlim();
                var poller = new Thread(() =>
                {
                    started.Set();
                    while (!Volatile.Read(ref stop))
                    {
                        foreach (var f in Directory.GetFiles(tempDir)) seen.TryAdd(Path.GetFileName(f), 0);
                        Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                poller.Start();
                started.Wait();
                long rows = 0;
                using (var query = c.CreateCommand())
                {
                    query.CommandText = "SELECT payload FROM big ORDER BY payload DESC";
                    using var reader = query.ExecuteReader();
                    while (reader.Read()) rows++;
                }
                Volatile.Write(ref stop, true);
                poller.Join();
                rows.Should().Be(20000);
                Thread.Sleep(200);
                DiagnosticLine = $"rows={rows} cache_size={c.ExecuteScalar<long>("PRAGMA cache_size")} dir_pragma={c.ExecuteScalar<string>("PRAGMA temp_store_directory")} files={string.Join(",", seen.Keys)} dir={tempDir}";
                return seen.Count;
            }
            finally
            {
                c.Execute("PRAGMA temp_store_directory = ''");
            }
        }

        [Fact]
        public void A_large_keyed_sort_leaves_no_file_in_the_temp_directory_while_the_same_sort_on_a_plain_connection_does()
        {
            using var plain = new DbConnectionFactory(Path.Combine(_dir, "plain"));
            using var plainConn = plain.CreateConnection();
            SortAndCountSpilledFiles(plainConn, _tempDir).Should().BeGreaterThan(0, "the control: with temp_store=FILE the sorter spills to disk (" + DiagnosticLine + ")");

            var otherTemp = Path.Combine(_dir, "sqlite-temp-keyed");
            Directory.CreateDirectory(otherTemp);
            using var keyed = new DbConnectionFactory(Path.Combine(_dir, "keyed"), new InMemoryVaultKeySource());
            using var keyedConn = keyed.CreateConnection();
            SortAndCountSpilledFiles(keyedConn, otherTemp).Should().Be(0, "with temp_store=MEMORY nothing is written outside the encrypted file");
        }
    }

    // ---------------------------------------------------------------- 15.1.3: key hygiene

    private sealed record Fixture(InMemoryVaultKeySource Keys, DbConnectionFactory Factory, byte[] Key);

    private Fixture OpenFixture()
    {
        var keys = new InMemoryVaultKeySource();
        var key = keys.GetPageKey(DbPurposes.Vault);
        var factory = new DbConnectionFactory(_dir, keys);
        return new Fixture(keys, factory, key);
    }

    private static List<KeyHit> Walk(Fixture f, bool includeProvider = true)
    {
        var roots = new List<(string, object?)> { ("keys", f.Keys), ("factory", f.Factory) };
        if (includeProvider) roots.Add(("provider", KeyGraphWalker.ProviderRegistry()));
        return KeyGraphWalker.Find(roots, f.Key);
    }

    [Fact]
    public async Task Key_hygiene_a_no_boundary_the_factory_exposes_contains_the_key_in_any_state()
    {
        var f = OpenFixture();
        using var _ = f.Factory;
        var hex = Convert.ToHexString(f.Key);
        var seen = new List<string>();
        void Check(string what, string? text)
        {
            seen.Add(what);
            (text ?? "").Should().NotContainEquivalentOf(hex, $"{what} must not carry the page key");
        }

        Check("DatabaseId (open, before any connection)", f.Factory.DatabaseId);
        using (var c = f.Factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");
        Check("DatabaseId (open)", f.Factory.DatabaseId);
        Check("factory.ToString()", f.Factory.ToString());
        Check("key source ToString()", f.Keys.ToString());
        Check("DataSource", SqlCipherScheme.DataSource(f.Factory.FilePath));
        f.Factory.DatabaseId.Should().Be(SqlCipherScheme.DataSource(f.Factory.FilePath), "the cache key of FolderAccessService is the key-free data source");

        // a wrong key
        var wrong = new DbConnectionFactory(_dir, new InMemoryVaultKeySource());
        Exception? mismatch = null;
        try { wrong.CreateConnection(); } catch (Exception e) { mismatch = e; }
        mismatch.Should().BeOfType<VaultKeyMismatchException>();
        Check("mismatch exception", mismatch!.ToString());
        Check("mismatch DatabaseId", wrong.DatabaseId);
        wrong.Dispose();

        await f.Factory.CloseForSealAsync(TimeSpan.FromSeconds(5));
        f.Keys.Close();
        Exception? closed = null;
        try { f.Factory.CreateConnection(); } catch (Exception e) { closed = e; }
        closed.Should().BeOfType<VaultSealedException>();
        Check("closed exception", closed!.ToString());
        Check("DatabaseId (closed)", f.Factory.DatabaseId);
        seen.Should().HaveCountGreaterThan(5);
    }

    [Fact]
    public void Key_hygiene_b_while_open_the_key_is_in_the_key_source_and_in_no_string_of_the_factory_or_the_provider()
    {
        var f = OpenFixture();
        using var _ = f.Factory;
        using var c1 = f.Factory.CreateConnection();
        using var c2 = f.Factory.CreateConnection();
        Run(c1, "CREATE TABLE t (x INTEGER)");
        using var staging = f.Factory.OpenFile(f.Factory.FilePath, readOnly: true);

        // The positive control: the walk does find the key where it must be (the gate's copy, here the in-memory key source).
        var hits = Walk(f);

        hits.Should().Contain(h => h.Path.StartsWith("keys"), "the key source holds the page key while the vault is open");
        hits.Where(h => !h.Path.StartsWith("keys")).Should().BeEmpty(
            "the key is applied to each native connection and never written into a connection string, so neither the factory, nor a live connection, nor Microsoft.Data.Sqlite's pool registry holds it");
        var live = KeyGraphWalker.Find([("c1", c1), ("c2", c2), ("staging", staging)], f.Key, doNotEnter: [f.Keys, f.Factory]);
        live.Should().BeEmpty(string.Join("; ", live));
    }

    [Fact]
    public async Task Key_hygiene_c_once_closed_the_key_is_nowhere_and_a_second_open_with_another_key_leaves_no_trace_of_the_first()
    {
        var f = OpenFixture();
        using var _ = f.Factory;
        using (var c = f.Factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");
        Walk(f).Should().Contain(h => h.Path.StartsWith("keys"));

        await f.Factory.CloseForSealAsync(TimeSpan.FromSeconds(5));
        Walk(f).Where(h => !h.Path.StartsWith("keys")).Should().BeEmpty("flushed before the gate may publish Closed");
        f.Keys.Close();
        Walk(f).Should().BeEmpty("a closed node holds the key nowhere, the provider's pool registry included");
        SqliteConnection.ClearAllPools();
        File.Move(DbPath, DbPath + ".moved");
        File.Move(DbPath + ".moved", DbPath);

        // Second open, a different key (a re-keyed vault): the old key is nowhere, the new one only in the key source.
        var newVault = RandomNumberGenerator.GetBytes(32);
        var secondFile = Path.Combine(_dir, "second");
        using var second = new DbConnectionFactory(secondFile, f.Keys);
        f.Keys.Open(newVault, RandomNumberGenerator.GetBytes(32));
        using (var c = second.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");
        KeyGraphWalker.Find([("keys", f.Keys), ("second", second), ("provider", KeyGraphWalker.ProviderRegistry())], f.Key).Should().BeEmpty("the first key is gone");
        var newKeyHits = KeyGraphWalker.Find([("second", second), ("provider", KeyGraphWalker.ProviderRegistry())], newVault, doNotEnter: [f.Keys]);
        newKeyHits.Should().BeEmpty(string.Join("; ", newKeyHits));
        KeyGraphWalker.Find([("keys", f.Keys)], newVault).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Key_hygiene_a_straggler_connection_is_the_only_holder_and_is_cleaned_when_it_returns()
    {
        var f = OpenFixture();
        using var _ = f.Factory;
        var straggler = f.Factory.CreateConnection();
        Run(straggler, "CREATE TABLE t (x INTEGER)");

        (await f.Factory.CloseForSealAsync(TimeSpan.FromMilliseconds(200))).Should().BeFalse();
        f.Keys.Close();

        Walk(f).Should().BeEmpty("the key lives in the native connection only, which no managed walk can read");
        straggler.Dispose();
        Walk(f).Should().BeEmpty();
        File.Move(DbPath, DbPath + ".moved");
        File.Move(DbPath + ".moved", DbPath);
    }

    // ---------------------------------------------------------------- 15.1.4: no plain fallback

    [Fact]
    public void A_keyed_factory_over_a_plain_file_fails_and_never_opens_it_silently()
    {
        using (var plain = new DbConnectionFactory(_dir))
        using (var c = plain.CreateConnection())
            Run(c, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1)");
        SqliteConnection.ClearAllPools();
        var before = Sha256(DbPath);

        using var keyed = new DbConnectionFactory(_dir, new InMemoryVaultKeySource());
        var act = () => keyed.CreateConnection();

        act.Should().Throw<VaultKeyMismatchException>();
        SqliteConnection.ClearAllPools();
        Sha256(DbPath).Should().Be(before);
        HasMagic(DbPath).Should().BeTrue("the plain file is untouched");
    }

    [Fact]
    public void A_plain_factory_over_a_sealed_file_fails_and_never_opens_it_silently()
    {
        using (var sealedFactory = new DbConnectionFactory(_dir, new InMemoryVaultKeySource()))
        using (var c = sealedFactory.CreateConnection())
            Run(c, "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1)");
        SqliteConnection.ClearAllPools();
        var before = Sha256(DbPath);

        using var plain = new DbConnectionFactory(_dir);
        var act = () => plain.CreateConnection();

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(26, "file is not a database");
        plain.OpenConnections.Should().Be(0);
        SqliteConnection.ClearAllPools();
        Sha256(DbPath).Should().Be(before);
    }

    [Fact]
    public async Task No_code_path_of_the_factory_catches_the_sealed_exception_and_retries_unkeyed()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");
        await factory.CloseForSealAsync(TimeSpan.FromSeconds(5));
        keys.Close();
        SqliteConnection.ClearAllPools();
        var before = Sha256(DbPath);

        for (var i = 0; i < 3; i++)
        {
            var create = () => factory.CreateConnection();
            create.Should().Throw<VaultSealedException>();
            var open = () => factory.OpenFile(DbPath, readOnly: false);
            open.Should().Throw<VaultSealedException>();
        }

        Sha256(DbPath).Should().Be(before);
    }

    private sealed class FixedModeKeySource(VaultStoreMode mode) : IVaultKeySource
    {
        public VaultStoreMode Mode => mode;
        public bool IsOpen => false;
        public long OpenGeneration => 0;
        public byte[] GetPageKey(string purpose) => throw new VaultSealedException();
    }

    [Fact]
    public async Task CloseForSealAsync_ends_the_generation_even_when_another_flow_holds_the_quiesce()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) Run(c, "CREATE TABLE t (x INTEGER)");
        // Another flow (a restore, a file switch) holds the barrier of this factory when the close arrives (review round 1, H4).
        var switching = await Task.Run(() => factory.BeginQuiesce());

        bool clean;
        try
        {
            clean = await factory.CloseForSealAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            switching.Dispose();
        }

        clean.Should().BeTrue("no connection of the generation was open");
        keys.IsOpen.Should().BeTrue();
        var act = () => factory.CreateConnection();
        act.Should().Throw<VaultSealedException>("the generation ended although the other flow still held the quiesce");
    }

    [Fact]
    public async Task CloseForSealAsync_with_the_quiesce_held_by_another_flow_still_waits_for_the_open_connections_and_reports_a_straggler()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new DbConnectionFactory(_dir, keys);
        var held = factory.CreateConnection();
        Run(held, "CREATE TABLE t (x INTEGER)");
        var switching = await Task.Run(() => factory.BeginQuiesce());
        try
        {
            var clean = await factory.CloseForSealAsync(TimeSpan.FromMilliseconds(300));

            clean.Should().BeFalse("one connection was still in use at the limit");
        }
        finally
        {
            switching.Dispose();
            held.Dispose();
        }

        var again = () => factory.CreateConnection();
        again.Should().Throw<VaultSealedException>();
    }
}
