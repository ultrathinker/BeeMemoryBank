using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// What the code that stages copies of a vault asks of the factory (docs/design/step-b-spec.md 7.2, 7.4, 8.4): is this node sealed, a copy of
/// its page key for the page codec, and the <c>ATTACH</c> statement for a staging copy (same key, no <c>KEY</c> clause) or for a plain
/// package database (<c>KEY ''</c>).
/// </summary>
public sealed class StagedFileFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_staged_" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryVaultKeySource _keys = new();

    public StagedFileFactoryTests() => Directory.CreateDirectory(_dir);

    // No ClearAllPools: the pool is process-wide and other tests of this assembly run beside this one.
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private DbConnectionFactory SealedVault(int rows = 5)
    {
        var factory = new DbConnectionFactory(Path.Combine(_dir, "beememorybank.db"), _keys);
        using var c = factory.CreateConnection();
        c.Execute("CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT)");
        for (var i = 0; i < rows; i++) c.Execute("INSERT INTO tbl_article (title) VALUES (@t)", new { t = "note " + i });
        return factory;
    }

    private string PlainFile(string name, int rows)
    {
        var path = Path.Combine(_dir, name);
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        c.Execute("CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT)");
        for (var i = 0; i < rows; i++) c.Execute("INSERT INTO tbl_article (title) VALUES (@t)", new { t = "plain " + i });
        return path;
    }

    [Fact]
    public void A_sealed_factory_says_so_hands_out_a_copy_of_its_page_key_and_a_closed_one_throws()
    {
        using var factory = SealedVault();

        factory.IsSealed.Should().BeTrue();
        var copy = factory.CopyPageKey()!;
        copy.Should().Equal(_keys.GetPageKey(DbPurposes.Vault));
        Array.Clear(copy);
        factory.CopyPageKey()!.Should().Equal(_keys.GetPageKey(DbPurposes.Vault), "the factory's own key was not touched when the copy was cleared");

        _keys.Close();
        var closed = () => factory.CopyPageKey();
        closed.Should().Throw<VaultSealedException>();
        factory.IsSealed.Should().BeTrue("a closed vault is still a sealed one");
    }

    [Fact]
    public void A_plain_factory_is_not_sealed_has_no_page_key_and_writes_the_statement_every_caller_always_wrote()
    {
        var path = PlainFile("plain.db", 1);
        using var factory = new DbConnectionFactory(Path.Combine(_dir, "other.db"));

        factory.IsSealed.Should().BeFalse();
        factory.CopyPageKey().Should().BeNull();
        factory.AttachStatement(path, "snap").Should().Be($"ATTACH DATABASE '{path}' AS snap");
        factory.AttachStatement(@"C:\it's\x.db", "snap").Should().Be(@"ATTACH DATABASE 'C:\it''s\x.db' AS snap");
    }

    [Fact]
    public void A_staging_copy_of_the_sealed_vault_attaches_under_the_vault_key_and_reads_back()
    {
        using var factory = SealedVault(rows: 7);
        var staging = Path.Combine(_dir, "staging.tmp");
        using (var c = (SqliteConnection)factory.CreateConnection())
            c.Execute($"VACUUM INTO '{staging}'");

        using var main = (SqliteConnection)factory.CreateConnection();
        var statement = factory.AttachStatement(staging, "snap");
        statement.Should().Contain("cipher=sqlcipher&legacy=4").And.Contain($"KEY \"{SqlCipherScheme.Password(_keys.GetPageKey(DbPurposes.Vault))}\"");
        main.Execute(statement);

        main.ExecuteScalar<long>("SELECT COUNT(*) FROM snap.tbl_article").Should().Be(7);
        main.Execute("DETACH DATABASE snap");
    }

    [Fact]
    public void A_closed_sealed_factory_cannot_write_an_attach_statement_for_a_staged_copy()
    {
        using var factory = SealedVault();
        var staging = Path.Combine(_dir, "staging-closed.tmp");
        using (var c = (SqliteConnection)factory.CreateConnection())
            c.Execute($"VACUUM INTO '{staging}'");
        _keys.Close();

        var act = () => factory.AttachStatement(staging, "snap");

        act.Should().Throw<VaultSealedException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_copy_made_by_the_page_codec_attaches_under_the_vault_key_like_a_vacuum_into_copy(bool streamed)
    {
        using var factory = SealedVault(rows: 9);
        var vacuumed = Path.Combine(_dir, "vacuumed.tmp");
        using (var c = (SqliteConnection)factory.CreateConnection())
            c.Execute($"VACUUM INTO '{vacuumed}'");
        byte[] image;
        var key = factory.CopyPageKey()!;
        await using (var reader = PlainImageReader.Open(vacuumed, key))
        {
            image = new byte[reader.Length];
            await reader.ReadExactlyAsync(image);
        }
        if (!streamed)
        {
            // An image of a 2.5 node: no page reserve, imported through the in-memory path.
            var plainPath = Path.Combine(_dir, "plain-image.sqlite");
            await File.WriteAllBytesAsync(plainPath, image);
            using (var plain = new SqliteConnection($"Data Source={plainPath};Pooling=False"))
            {
                plain.Open();
                plain.Execute("VACUUM");
            }
            image = await File.ReadAllBytesAsync(plainPath);
        }
        var staged = Path.Combine(_dir, "staged-by-codec.tmp");
        await PlainImageImporter.ToSealedFileAsync(new MemoryStream(image), image.Length, staged, key, memoryCap: 64L << 20);

        using var main = (SqliteConnection)factory.CreateConnection();
        main.Execute(factory.AttachStatement(staged, "snap"));

        main.ExecuteScalar<long>("SELECT COUNT(*) FROM snap.tbl_article").Should().Be(9);
        main.Execute("DETACH DATABASE snap");
    }

    [Fact]
    public void A_plain_package_database_attaches_to_the_sealed_connection_with_an_empty_key_and_a_bare_attach_is_refused()
    {
        using var factory = SealedVault();
        var package = PlainFile("package.db", 3);

        using var main = (SqliteConnection)factory.CreateConnection();
        var bare = () => main.Execute($"ATTACH DATABASE '{package}' AS bare");
        bare.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(26, "the engine refuses a plain file without a KEY clause");

        var statement = factory.AttachStatement(package, "snap");
        statement.Should().EndWith("KEY ''");
        main.Execute(statement);
        main.ExecuteScalar<long>("SELECT COUNT(*) FROM snap.tbl_article").Should().Be(3);
        main.Execute("DETACH DATABASE snap");
    }

    [Fact]
    public void OpenFile_opens_a_staging_copy_keyed_and_a_plain_package_unkeyed_never_the_other_way_round()
    {
        using var factory = SealedVault(rows: 4);
        var staging = Path.Combine(_dir, "staging2.tmp");
        using (var c = (SqliteConnection)factory.CreateConnection())
            c.Execute($"VACUUM INTO '{staging}'");

        using (var keyed = factory.OpenFile(staging, readOnly: true))
            keyed.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().Be(4);

        var package = PlainFile("package2.db", 2);
        var plainThroughSealed = () => factory.OpenFile(package, readOnly: true);
        plainThroughSealed.Should().Throw<VaultKeyMismatchException>("a plain package is read by a plain connection, not by the vault's key");
        using (var plain = new SqliteConnection($"Data Source={package};Mode=ReadOnly;Pooling=False"))
        {
            plain.Open();
            plain.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().Be(2);
        }
    }
}
