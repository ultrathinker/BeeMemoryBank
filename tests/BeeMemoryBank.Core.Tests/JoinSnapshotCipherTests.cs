using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Metadata;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>What a joiner does with the database entry of a join archive (docs/design/step-a-spec.md 11 step 3).</summary>
public class JoinSnapshotCipherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bmb-joincipher-").FullName;
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(32);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* the test's own temp folder */ }
    }

    private string PlainDatabase(bool withActivation = false, string name = "plain.db")
    {
        var path = Path.Combine(_dir, name);
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE tbl_article (id INTEGER, title TEXT); INSERT INTO tbl_article VALUES (1, 'a note');"
            + (withActivation ? "CREATE TABLE tbl_metadata_activation (activation_id TEXT); INSERT INTO tbl_metadata_activation VALUES ('x');" : "");
        cmd.ExecuteNonQuery();
        return path;
    }

    private async Task<string> SealedAsync(byte[]? key = null, string? label = null, string purpose = ChunkedContainer.PurposeJoin)
    {
        var plain = PlainDatabase(name: $"p-{Guid.NewGuid():N}.db");
        var path = Path.Combine(_dir, $"sealed-{Guid.NewGuid():N}.db");
        await using (var input = File.OpenRead(plain))
        await using (var output = File.Create(path))
            await ChunkedContainer.EncryptAsync(input, output, purpose, label ?? ChunkedContainer.DekKeyLabel(DekFingerprint.Of(_dek)), key ?? _dek);
        return path;
    }

    private static string CountArticles(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT title FROM tbl_article";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task A_sealed_database_is_opened_in_place_with_the_master_key()
    {
        var path = await SealedAsync();
        File.ReadAllBytes(path).AsSpan(0, 8).SequenceEqual("BMBCH1\0\0"u8).Should().BeTrue();

        await JoinSnapshotCipher.OpenAsync(JoinSnapshotCipher.Cipher, path, _dek, requireEncrypted: true);

        CountArticles(path).Should().Be("a note");
        Directory.GetFiles(_dir, "*.opened").Should().BeEmpty();
    }

    [Fact]
    public async Task Another_master_key_is_refused_with_a_sentence_and_the_file_is_left_alone()
    {
        var path = await SealedAsync();
        var before = File.ReadAllBytes(path);

        var act = () => JoinSnapshotCipher.OpenAsync(JoinSnapshotCipher.Cipher, path, RandomNumberGenerator.GetBytes(32), requireEncrypted: true);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("could not be opened with this vault's master key");
        File.ReadAllBytes(path).Should().Equal(before);
        Directory.GetFiles(_dir, "*.opened").Should().BeEmpty();
    }

    [Fact]
    public async Task A_container_sealed_under_another_key_but_labelled_with_ours_is_refused()
    {
        var path = await SealedAsync(key: RandomNumberGenerator.GetBytes(32));

        var act = () => JoinSnapshotCipher.OpenAsync(JoinSnapshotCipher.Cipher, path, _dek, requireEncrypted: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_container_of_another_purpose_is_refused()
    {
        var path = await SealedAsync(purpose: ChunkedContainer.PurposeBaseline);

        var act = () => JoinSnapshotCipher.OpenAsync(JoinSnapshotCipher.Cipher, path, _dek, requireEncrypted: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_damaged_container_is_refused()
    {
        var path = await SealedAsync();
        var bytes = File.ReadAllBytes(path);
        bytes[^5] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        var act = () => JoinSnapshotCipher.OpenAsync(JoinSnapshotCipher.Cipher, path, _dek, requireEncrypted: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetFiles(_dir, "*.opened").Should().BeEmpty();
    }

    [Fact]
    public async Task A_sealed_archive_with_no_master_key_to_open_it_is_refused()
    {
        var path = await SealedAsync();

        var act = () => JoinSnapshotCipher.OpenAsync(JoinSnapshotCipher.Cipher, path, masterDek: null, requireEncrypted: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("no master key");
    }

    [Fact]
    public async Task A_cipher_this_version_does_not_know_is_refused()
    {
        var act = () => JoinSnapshotCipher.OpenAsync("bmbch9", PlainDatabase(), _dek, requireEncrypted: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("update this device");
    }

    [Fact]
    public async Task A_plain_archive_is_taken_when_the_host_does_not_seal_and_holds_no_activation()
    {
        var path = PlainDatabase();

        await JoinSnapshotCipher.OpenAsync(null, path, masterDek: null, requireEncrypted: false);

        CountArticles(path).Should().Be("a note");
    }

    [Fact]
    public async Task A_plain_archive_is_refused_when_the_host_declared_protocol_4()
    {
        var act = () => JoinSnapshotCipher.OpenAsync(null, PlainDatabase(), _dek, requireEncrypted: true);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("plain snapshot");
    }

    [Fact]
    public async Task A_plain_archive_that_holds_an_activation_is_refused_whatever_the_host_declared()
    {
        var act = () => JoinSnapshotCipher.OpenAsync(null, PlainDatabase(withActivation: true), _dek, requireEncrypted: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("encrypts its metadata");
    }

    [Fact]
    public void The_manifest_reader_finds_the_cipher_only_as_a_string()
    {
        static string? Cipher(string json) { using var doc = System.Text.Json.JsonDocument.Parse(json); return JoinSnapshotCipher.CipherOf(doc.RootElement); }

        Cipher("""{"dbCipher":"bmbch1"}""").Should().Be("bmbch1");
        Cipher("""{"dbEncrypted":true}""").Should().BeNull();
        Cipher("""{"dbCipher":null}""").Should().BeNull();
        Cipher("""{"dbCipher":1}""").Should().BeNull();
    }
}
