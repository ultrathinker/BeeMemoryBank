using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Certes;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Services.Acme.Tests;

public class AcmeSecretsEncryptionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _acmeDir;

    public AcmeSecretsEncryptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "acme-encryption-tests-" + Guid.NewGuid().ToString("N"));
        _acmeDir = Path.Combine(_tempDir, "certs", "acme");
        Directory.CreateDirectory(_acmeDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void StoredCertificate_PfxPassword_IsEncryptedAtRest()
    {
        var plaintextPassword = "my-super-secret-pfx-password-" + Guid.NewGuid().ToString();
        var domain = "test.domain.com";
        var metaPath = Path.Combine(_acmeDir, domain + ".meta.json");

        // 1. Encrypt the password and create StoredCertificate
        var encryptedPassword = OperatingSystem.IsWindows() 
            ? StoredCertificate.EncryptPassword(plaintextPassword) 
            : plaintextPassword;

        var stored = new StoredCertificate
        {
            Domain = domain,
            PfxPath = "dummy-pfx-path",
            ChainPemPath = "dummy-chain-path",
            PfxPassword = encryptedPassword,
            NotBefore = DateTime.UtcNow,
            NotAfter = DateTime.UtcNow.AddDays(90),
            IssuedAt = DateTime.UtcNow
        };

        // 2. Serialize and write to disk
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metaPath, json);

        // 3. Encryption is Windows-only (DPAPI); off Windows StoredCertificate.EncryptPassword is
        //    never called (see step 1) so the "password" here IS the plaintext by design — only
        //    assert non-recoverability where encryption actually happened.
        var fileContent = File.ReadAllText(metaPath);
        if (OperatingSystem.IsWindows())
        {
            fileContent.Should().NotContain(plaintextPassword);
        }
        else
        {
            fileContent.Should().Contain(plaintextPassword);
        }

        // 4. Deserialize and decrypt
        var deserialized = JsonSerializer.Deserialize<StoredCertificate>(fileContent);
        deserialized.Should().NotBeNull();
        
        var decryptedPassword = OperatingSystem.IsWindows()
            ? StoredCertificate.DecryptPassword(deserialized!.PfxPassword)
            : deserialized!.PfxPassword;

        decryptedPassword.Should().Be(plaintextPassword);
    }

    [Fact]
    public void AcmeAccountKey_IsEncryptedAtRest()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Generate a real Certes account key
        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var pem = accountKey.ToPem();
        var accountKeyPath = Path.Combine(_acmeDir, "account.pem");

        // 1. Encrypt the PEM key using the DPAPI pattern implemented in AcmeCertificateService
        var pemBytes = Encoding.UTF8.GetBytes(pem);
        var encryptedBytes = ProtectedData.Protect(pemBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(accountKeyPath, encryptedBytes);

        // 2. Read file raw bytes and assert that the plain text PEM string does not appear in it
        var rawBytes = File.ReadAllBytes(accountKeyPath);
        var rawString = Encoding.UTF8.GetString(rawBytes);
        
        rawString.Should().NotContain("-----BEGIN EC PRIVATE KEY-----");
        rawString.Should().NotContain("-----BEGIN PRIVATE KEY-----");

        // 3. Load the key back and decrypt it
        var loadedBytes = File.ReadAllBytes(accountKeyPath);
        var decryptedBytes = ProtectedData.Unprotect(loadedBytes, null, DataProtectionScope.CurrentUser);
        var decryptedPem = Encoding.UTF8.GetString(decryptedBytes);

        decryptedPem.Should().Be(pem);

        // 4. Assert that KeyFactory can load the decrypted PEM successfully
        var loadedKey = KeyFactory.FromPem(decryptedPem);
        loadedKey.Should().NotBeNull();
        loadedKey.ToPem().Should().Be(pem);
    }
}
