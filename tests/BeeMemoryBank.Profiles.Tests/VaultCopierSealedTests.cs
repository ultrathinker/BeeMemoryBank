using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Profiles.Tests;

/// <summary>
/// <see cref="VaultCopier"/> with a sealed vault (docs/design/step-b-spec.md 7.6, red test 15.1.11): the boot store is copied like every other
/// non-transient file and the verification covers it; a folder that already holds a sealed vault is not an empty target.
/// </summary>
public sealed class VaultCopierSealedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BmbVaultCopierSealed_" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "vault");

    public VaultCopierSealedTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllBytes(Path.Combine(Source, "beememorybank.db"), RandomNumberGenerator.GetBytes(8192));
        File.WriteAllBytes(Path.Combine(Source, "chat.db"), RandomNumberGenerator.GetBytes(8192));
        var boot = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(boot);
        BinaryPrimitives.WriteInt32BigEndian(boot.AsSpan(68), 0x424D4253);
        RandomNumberGenerator.Fill(boot.AsSpan(100));
        File.WriteAllBytes(Path.Combine(Source, "beememorybank.boot.db"), boot);
        File.WriteAllText(Path.Combine(Source, "node.lock"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void The_boot_store_is_copied_with_the_sealed_files_byte_for_byte_and_the_folder_is_a_vault_afterwards()
    {
        var target = Path.Combine(_root, "new-place");

        var result = VaultCopier.CopyVerified(Source, target, progress: null, CancellationToken.None);

        result.FileCount.Should().Be(3, "the database, chat.db and the boot store; the lock file is runtime state");
        foreach (var name in new[] { "beememorybank.db", "chat.db", "beememorybank.boot.db" })
            File.ReadAllBytes(Path.Combine(target, name)).Should().Equal(File.ReadAllBytes(Path.Combine(Source, name)), name);
        File.Exists(Path.Combine(target, "node.lock")).Should().BeFalse();
        BeeMemoryBank.AppPaths.VaultFiles.Inspect(target).Should().Be(BeeMemoryBank.AppPaths.VaultFolderState.Vault);
        BeeMemoryBank.AppPaths.VaultFiles.Inspect(Source).Should().Be(BeeMemoryBank.AppPaths.VaultFolderState.Vault);
    }

    [Fact]
    public void A_folder_that_already_holds_a_sealed_vault_is_not_a_target()
    {
        var other = Path.Combine(_root, "other-vault");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "beememorybank.db"), RandomNumberGenerator.GetBytes(8192));
        File.Copy(Path.Combine(Source, "beememorybank.boot.db"), Path.Combine(other, "beememorybank.boot.db"));

        VaultCopier.ValidateTarget(Source, other).Should().Be("This folder already contains a Bee Memory Bank profile.");
        var act = () => VaultCopier.CopyVerified(Source, other, null, CancellationToken.None);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_copy_whose_boot_store_went_missing_is_not_an_empty_target_for_another_copy()
    {
        var target = Path.Combine(_root, "second");
        VaultCopier.CopyVerified(Source, target, null, CancellationToken.None);
        new FileInfo(Path.Combine(target, "beememorybank.boot.db")).Length.Should().Be(4096, "the verification of the copy compared its size with the source's");

        File.Delete(Path.Combine(target, "beememorybank.boot.db"));

        VaultCopier.ValidateTarget(Source, target).Should().Be("Choose an empty folder.", "the encrypted database is still there, so the folder is not empty");
    }
}
