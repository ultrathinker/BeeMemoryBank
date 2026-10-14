using System.Data;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The chat database factory on a sealed node (docs/design/step-b-spec.md 7.2, 9, red tests 15.1.1-15.1.4 for the chat purpose):
/// <c>chat.db</c> is page-encrypted under its own page key, closed means <see cref="VaultSealedException"/>, the keep-alive connection
/// lives for one open generation, and the key is nowhere once the generation has been closed.
/// </summary>
public sealed class SealedChatDbConnectionFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_chatdb_sealed_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string ChatPath => Path.Combine(_dir, "chat.db");

    private static bool HasMagic(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[16];
        return fs.Read(head) == 16 && head.AsSpan().SequenceEqual("SQLite format 3\0"u8);
    }

    [Fact]
    public void A_sealed_chat_database_holds_no_canary_and_no_sqlite_magic_in_any_of_its_files()
    {
        var canary = MetadataCanaries.Ascii("chattitle");
        var keys = new InMemoryVaultKeySource();
        using var factory = new ChatDbConnectionFactory(_dir, keys);

        using (var c = factory.CreateConnection())
        {
            c.Execute("CREATE TABLE chat_conversation (id INTEGER PRIMARY KEY, title TEXT)");
            c.Execute("INSERT INTO chat_conversation (title) VALUES (@canary)", new { canary });
        }
        using (var again = factory.CreateConnection())
            again.QuerySingle<string>("SELECT title FROM chat_conversation").Should().Be(canary);

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        files.Should().Contain(["chat.db", "chat.db-wal", "chat.db-shm"]);
        MetadataCanaries.ScanDirectory(_dir, [canary]).Should().BeEmpty("every file of the folder, the -wal and -shm included, is encrypted");
        HasMagic(ChatPath).Should().BeFalse();
        (new FileInfo(ChatPath).Length % 4096).Should().Be(0);
    }

    [Fact]
    public void The_same_chat_data_on_a_plain_factory_does_hold_the_canary_which_proves_the_scan_can_see_it()
    {
        var canary = MetadataCanaries.Ascii("chattitle");
        using var factory = new ChatDbConnectionFactory(_dir);
        using (var c = factory.CreateConnection())
        {
            c.Execute("CREATE TABLE chat_conversation (id INTEGER PRIMARY KEY, title TEXT)");
            c.Execute("INSERT INTO chat_conversation (title) VALUES (@canary)", new { canary });
        }

        MetadataCanaries.ScanDirectory(_dir, [canary]).Should().NotBeEmpty();
        HasMagic(ChatPath).Should().BeTrue();
    }

    [Fact]
    public void The_chat_page_key_and_the_vault_page_key_are_two_keys_and_a_page_never_decrypts_in_the_other_file()
    {
        var vaultKey = new InMemoryVaultKeySource();
        using (var chat = new ChatDbConnectionFactory(_dir, vaultKey))
        using (var c = chat.CreateConnection())
            c.Execute("CREATE TABLE t (x INTEGER)");
        SqliteConnection.ClearAllPools();

        // The vault factory pointed at chat.db uses the vault key and must not open it.
        using var wrongPurpose = new DbConnectionFactory(ChatPath, vaultKey, DbPurposes.Vault);
        var act = () => wrongPurpose.CreateConnection();

        act.Should().Throw<VaultKeyMismatchException>();
    }

    [Fact]
    public void A_closed_key_source_throws_VaultSealedException_creates_no_file_and_opens_no_keep_alive()
    {
        var keys = new InMemoryVaultKeySource();
        keys.Close();

        using var factory = new ChatDbConnectionFactory(_dir, keys);
        var act = () => factory.CreateConnection();

        act.Should().Throw<VaultSealedException>();
        File.Exists(ChatPath).Should().BeFalse("a sealed node never opens chat.db at construction, and not while closed");
        factory.OpenConnections.Should().Be(0);
    }

    [Fact]
    public void A_sealed_node_opens_the_keep_alive_with_the_first_connection_and_not_at_construction()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new ChatDbConnectionFactory(_dir, keys);

        File.Exists(ChatPath).Should().BeFalse("nothing is opened at construction");
        factory.OpenConnections.Should().Be(0);

        using (var c = factory.CreateConnection()) c.Execute("CREATE TABLE t (x INTEGER)");

        factory.OpenConnections.Should().Be(1, "the keep-alive connection of this open generation");
        File.Exists(ChatPath).Should().BeTrue();
    }

    [Fact]
    public void A_plain_node_opens_its_keep_alive_at_construction_exactly_as_before()
    {
        // The oracle: what a bare connection does to the folder on this platform.
        var oracleDir = Path.Combine(_dir, "oracle");
        Directory.CreateDirectory(oracleDir);
        using (var oracle = new SqliteConnection($"Data Source={Path.Combine(oracleDir, "chat.db")}")) oracle.Open();
        var oracleHasFile = File.Exists(Path.Combine(oracleDir, "chat.db"));

        using var factory = new ChatDbConnectionFactory(Path.Combine(_dir, "plain"));

        File.Exists(Path.Combine(_dir, "plain", "chat.db")).Should().Be(oracleHasFile);
        factory.OpenConnections.Should().Be(0, "the plain keep-alive is a bare connection, not counted in the drain");
        using var c = factory.CreateConnection();
        c.QuerySingle<int>("PRAGMA temp_store").Should().Be(1, "1 = FILE, as before Step B");
    }

    [Fact]
    public void A_sealed_file_found_without_a_key_source_is_left_alone_at_construction()
    {
        using (var seed = new ChatDbConnectionFactory(_dir, new InMemoryVaultKeySource()))
        using (var c = seed.CreateConnection())
            c.Execute("CREATE TABLE t (x INTEGER)");
        SqliteConnection.ClearAllPools();

        // The registration of ApiServices.cs builds the factory before the sealed-vault services replace it: it must not touch the file.
        using var factory = new ChatDbConnectionFactory(_dir);

        if (OperatingSystem.IsWindows())
        {
            using var exclusive = new FileStream(ChatPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            exclusive.Length.Should().BeGreaterThan(0);
        }
        var act = () => factory.CreateConnection();
        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(26, "a plain factory never opens a sealed file");
    }

    [Fact]
    public async Task BeginQuiesce_drops_the_keep_alive_so_the_drain_finishes_and_CloseForSealAsync_flushes_the_generation()
    {
        var keys = new InMemoryVaultKeySource();
        using var factory = new ChatDbConnectionFactory(_dir, keys);
        using (var c = factory.CreateConnection()) c.Execute("CREATE TABLE t (x INTEGER)");
        factory.OpenConnections.Should().Be(1);

        using (factory.BeginQuiesce())
        {
            await factory.WaitDrainedAsync(TimeSpan.FromSeconds(5));
            factory.OpenConnections.Should().Be(0);
            using (var last = factory.CreateConnection()) last.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            (await factory.CloseForSealAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        var act = () => factory.CreateConnection();
        act.Should().Throw<VaultSealedException>("the generation is over although the key source still says open");
        factory.OpenConnections.Should().Be(0);
        SqliteConnection.ClearAllPools();
        File.Move(ChatPath, ChatPath + ".moved");
        File.Move(ChatPath + ".moved", ChatPath);
    }

    [Fact]
    public async Task Key_hygiene_the_chat_key_is_in_the_key_source_while_open_and_nowhere_once_closed()
    {
        var keys = new InMemoryVaultKeySource();
        var key = keys.GetPageKey(DbPurposes.Chat);
        var hex = Convert.ToHexString(key);
        using var factory = new ChatDbConnectionFactory(_dir, keys);
        using var held = factory.CreateConnection();
        held.Execute("CREATE TABLE t (x INTEGER)");

        factory.DatabaseId.Should().NotContainEquivalentOf(hex);
        factory.ToString().Should().NotContainEquivalentOf(hex);
        var open = KeyGraphWalker.Find([("keys", keys), ("factory", factory), ("held", held), ("provider", KeyGraphWalker.ProviderRegistry())], key);
        open.Should().Contain(h => h.Path.StartsWith("keys"));
        open.Where(h => !h.Path.StartsWith("keys")).Should().BeEmpty(string.Join("; ", open));

        held.Dispose();
        (await factory.CloseForSealAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        keys.Close();

        KeyGraphWalker.Find([("keys", keys), ("factory", factory), ("provider", KeyGraphWalker.ProviderRegistry())], key).Should().BeEmpty();
    }
}
