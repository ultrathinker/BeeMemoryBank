using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>
/// <see cref="VaultFiles.Inspect"/> for a sealed vault (docs/design/step-b-spec.md 7.6, red test 15.1.11): its database has no SQLite header,
/// so the folder is recognised by the boot store - the SQLite magic and our application id - and a foreign boot store is not a vault.
/// </summary>
public sealed class VaultFilesSealedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BmbVaultFilesSealed_" + Guid.NewGuid().ToString("N"));

    public VaultFilesSealedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    /// <summary>The 100-byte header of a SQLite file with the given application id (bytes 68-71); all the probe reads.</summary>
    private static byte[] Header(int applicationId)
    {
        var bytes = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(68), applicationId);
        return bytes;
    }

    private static void WriteSealedDb(string folder) => File.WriteAllBytes(Path.Combine(folder, "beememorybank.db"), RandomNumberGenerator.GetBytes(8192));

    [Fact]
    public void A_folder_with_a_boot_store_of_ours_and_an_encrypted_database_is_a_vault()
    {
        var folder = Folder("sealed");
        WriteSealedDb(folder);
        File.WriteAllBytes(Path.Combine(folder, "beememorybank.boot.db"), Header(0x424D4253));

        VaultFiles.Inspect(folder).Should().Be(VaultFolderState.Vault);
    }

    [Fact]
    public void A_folder_with_only_a_boot_store_of_ours_is_still_a_vault_folder()
    {
        var folder = Folder("boot-only");
        File.WriteAllBytes(Path.Combine(folder, "beememorybank.boot.db"), Header(0x424D4253));

        VaultFiles.Inspect(folder).Should().Be(VaultFolderState.Vault);
    }

    [Fact]
    public void A_folder_with_a_foreign_boot_store_is_other()
    {
        var foreignId = Folder("foreign-id");
        File.WriteAllBytes(Path.Combine(foreignId, "beememorybank.boot.db"), Header(0));
        WriteSealedDb(foreignId);
        VaultFiles.Inspect(foreignId).Should().Be(VaultFolderState.Other, "a SQLite file without our application id is not our boot store");

        var noMagic = Folder("no-magic");
        File.WriteAllBytes(Path.Combine(noMagic, "beememorybank.boot.db"), RandomNumberGenerator.GetBytes(4096));
        VaultFiles.Inspect(noMagic).Should().Be(VaultFolderState.Other);
    }

    [Fact]
    public void An_encrypted_database_without_its_boot_store_is_not_a_vault_folder()
    {
        var folder = Folder("no-boot");
        WriteSealedDb(folder);

        VaultFiles.Inspect(folder).Should().Be(VaultFolderState.Other);
    }

    [Fact]
    public void A_plain_vault_folder_is_still_a_vault_and_an_empty_one_still_empty()
    {
        var plain = Folder("plain");
        File.WriteAllBytes(Path.Combine(plain, "beememorybank.db"), Header(0));
        VaultFiles.Inspect(plain).Should().Be(VaultFolderState.Vault);
        VaultFiles.Inspect(Folder("empty")).Should().Be(VaultFolderState.Empty);
        VaultFiles.Inspect(Path.Combine(_root, "missing")).Should().Be(VaultFolderState.Missing);
    }
}
