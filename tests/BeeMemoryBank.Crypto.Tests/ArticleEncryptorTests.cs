using BeeMemoryBank.Crypto;
using System.Security.Cryptography;

namespace BeeMemoryBank.Crypto.Tests;

public class ArticleEncryptorTests
{
    [Fact]
    public void EncryptDecrypt_Roundtrip()
    {
        var dek = DekManager.GenerateArticleDek();
        var plaintext = "Hello, World! Привет, мир!";

        var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, dek);
        var decrypted = ArticleEncryptor.Decrypt(ciphertext, iv, dek);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_DifferentIvEachTime()
    {
        var dek = DekManager.GenerateArticleDek();
        var plaintext = "test";

        var (_, iv1) = ArticleEncryptor.Encrypt(plaintext, dek);
        var (_, iv2) = ArticleEncryptor.Encrypt(plaintext, dek);

        iv1.Should().NotEqual(iv2);
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var dek = DekManager.GenerateArticleDek();
        var wrongDek = DekManager.GenerateArticleDek();

        var (ciphertext, iv) = ArticleEncryptor.Encrypt("text", dek);

        var act = () => ArticleEncryptor.Decrypt(ciphertext, iv, wrongDek);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var dek = DekManager.GenerateArticleDek();
        var (ciphertext, iv) = ArticleEncryptor.Encrypt("important text", dek);

        // Tamper with a byte in ciphertext
        ciphertext[0] ^= 0xFF;

        var act = () => ArticleEncryptor.Decrypt(ciphertext, iv, dek);
        act.Should().Throw<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void Encrypt_CyrillicText_Roundtrip()
    {
        var dek = DekManager.GenerateArticleDek();
        var plaintext = "Article in Russian with Cyrillic: абвгдеёжзийклмнопрстуфхцчшщъыьэюя";

        var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, dek);
        var decrypted = ArticleEncryptor.Decrypt(ciphertext, iv, dek);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_EmptyString_Roundtrip()
    {
        var dek = DekManager.GenerateArticleDek();
        var (ciphertext, iv) = ArticleEncryptor.Encrypt("", dek);
        var decrypted = ArticleEncryptor.Decrypt(ciphertext, iv, dek);
        decrypted.Should().Be("");
    }

    [Fact]
    public void Encrypt_LargeText_Roundtrip()
    {
        var dek = DekManager.GenerateArticleDek();
        var plaintext = new string('A', 100_000); // 100K Cyrillic characters → ~200KB UTF-8

        var (ciphertext, iv) = ArticleEncryptor.Encrypt(plaintext, dek);
        var decrypted = ArticleEncryptor.Decrypt(ciphertext, iv, dek);

        decrypted.Should().Be(plaintext);
    }
}
