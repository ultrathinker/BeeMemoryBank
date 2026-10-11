using System.Security.Cryptography;
using BeeMemoryBank.Rekey.Steps;
using BeeMemoryBank.Storage.Sealed;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// <see cref="RekeyPreflight"/> finds the copies of a vault a re-key would leave readable (docs/design/step-b-spec.md 7.6, red test 15.1.11).
/// A sealed copy has no SQLite header: a file of whole 4096-byte pages that opens with the vault's page key and holds the vault's tables is
/// reported as an encrypted copy that opens with the current key; a file of that shape that does not open is reported as one that may be an
/// encrypted copy and cannot be checked. The check must never silently stop finding copies.
/// </summary>
public sealed class RekeyPreflightEncryptedCopyTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "bmb_rekey_temp_" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bmb_rekey_data_" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public RekeyPreflightEncryptedCopyTests()
    {
        Directory.CreateDirectory(_temp);
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_temp, true); } catch { }
        try { Directory.Delete(_data, true); } catch { }
    }

    private static void MakeVaultLike(string path, byte[]? key, bool withVaultTables = true)
    {
        using var conn = key is null
            ? new SqliteConnection($"Data Source={path};Pooling=False")
            : SealedSqlite.OpenKeyed(path, key);
        if (key is null) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = withVaultTables
            ? "CREATE TABLE tbl_article_body(id INTEGER PRIMARY KEY, body BLOB); CREATE TABLE tbl_key_slot(id INTEGER PRIMARY KEY, slot BLOB); INSERT INTO tbl_key_slot(slot) VALUES (randomblob(2000));"
            : "CREATE TABLE unrelated(id INTEGER PRIMARY KEY, x TEXT); INSERT INTO unrelated(x) VALUES (hex(randomblob(2000)));";
        cmd.ExecuteNonQuery();
    }

    private List<string> Warnings(byte[]? pageKey)
    {
        var warnings = new List<string>();
        new RekeyPreflight { OsTempDir = _temp, VaultPageKey = pageKey }.ListLeftovers(_data, warnings);
        return warnings;
    }

    [Fact]
    public void A_sealed_copy_that_opens_with_the_current_key_is_reported_as_an_encrypted_copy_of_this_vault()
    {
        MakeVaultLike(Path.Combine(_temp, "bmb-restore-copy.tmp"), _key);

        var warnings = Warnings(_key);

        warnings.Should().ContainSingle().Which.Should().Contain("bmb-restore-copy.tmp").And.Contain("an encrypted copy of this vault (opens with the current key)");
    }

    [Fact]
    public void A_sealed_file_that_does_not_open_and_noise_of_whole_pages_are_reported_as_files_that_may_be_encrypted_copies()
    {
        MakeVaultLike(Path.Combine(_temp, "other-key.tmp"), RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(Path.Combine(_temp, "noise.bin"), RandomNumberGenerator.GetBytes(4096 * 3));

        var warnings = Warnings(_key);

        warnings.Should().HaveCount(2);
        warnings.Should().OnlyContain(w => w.Contains("may be an encrypted copy") && w.Contains("cannot be checked"));
        warnings.Should().Contain(w => w.Contains("other-key.tmp")).And.Contain(w => w.Contains("noise.bin"));
    }

    [Fact]
    public void Files_that_cannot_be_copies_are_left_alone()
    {
        MakeVaultLike(Path.Combine(_temp, "unrelated-sealed.tmp"), _key, withVaultTables: false); // opens, but is no vault
        File.WriteAllBytes(Path.Combine(_temp, "small.bin"), RandomNumberGenerator.GetBytes(300));
        File.WriteAllBytes(Path.Combine(_temp, "odd-length.bin"), RandomNumberGenerator.GetBytes(4096 * 2 + 17));
        File.WriteAllText(Path.Combine(_temp, "notes.txt"), new string('x', 9000));

        Warnings(_key).Should().BeEmpty();
    }

    [Fact]
    public void A_plain_copy_is_reported_exactly_as_before_for_a_plain_and_for_a_sealed_vault()
    {
        MakeVaultLike(Path.Combine(_temp, "plain-copy.tmp"), key: null);
        MakeVaultLike(Path.Combine(_temp, "plain-unrelated.tmp"), key: null, withVaultTables: false);

        foreach (var pageKey in new byte[]?[] { null, _key })
        {
            var warnings = Warnings(pageKey);
            warnings.Should().ContainSingle().Which.Should().Contain("plain-copy.tmp").And.Contain("a copy of a vault in the OS temp folder, under the old key");
        }
    }

    [Fact]
    public void A_plain_vault_re_key_does_not_flag_noise_it_has_no_key_to_open_it_with()
    {
        File.WriteAllBytes(Path.Combine(_temp, "noise.bin"), RandomNumberGenerator.GetBytes(4096 * 3));
        MakeVaultLike(Path.Combine(_temp, "sealed-by-someone.tmp"), _key);

        Warnings(pageKey: null).Should().BeEmpty("a plain vault's copies have the SQLite header; a headerless file is not judged without a key");
    }
}
