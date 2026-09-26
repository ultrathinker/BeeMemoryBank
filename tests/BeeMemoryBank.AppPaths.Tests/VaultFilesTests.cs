using System;
using System.IO;
using System.Linq;
using System.Text;
using BeeMemoryBank.AppPaths;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.AppPaths.Tests;

public sealed class VaultFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BmbVaultFiles_" + Guid.NewGuid().ToString("N"));

    public VaultFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Inspect_TellsMissingEmptyVaultAndOtherApart()
    {
        VaultFiles.Inspect(Path.Combine(_root, "missing")).Should().Be(VaultFolderState.Missing);

        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        VaultFiles.Inspect(empty).Should().Be(VaultFolderState.Empty);

        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "readme.txt"), "x");
        VaultFiles.Inspect(other).Should().Be(VaultFolderState.Other);

        // A file merely NAMED like the database is not a vault.
        var fake = Directory.CreateDirectory(Path.Combine(_root, "fake")).FullName;
        File.WriteAllText(Path.Combine(fake, VaultFiles.DatabaseFileName), "not sqlite");
        VaultFiles.Inspect(fake).Should().Be(VaultFolderState.Other);

        var vault = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName;
        File.WriteAllBytes(Path.Combine(vault, VaultFiles.DatabaseFileName),
            Encoding.ASCII.GetBytes("SQLite format 3\0").Concat(new byte[100]).ToArray());
        VaultFiles.Inspect(vault).Should().Be(VaultFolderState.Vault);
    }

    [Theory]
    [InlineData("node.lock", true)]
    [InlineData("NODE.STATUS.JSON", true)]
    [InlineData(".runtime.json", true)]
    [InlineData("api.ready", true)]
    [InlineData("beememorybank.db", false)]
    [InlineData("beememorybank.db-wal", false)]
    [InlineData(".internal-key", false)]
    public void IsTransient_OnlyMatchesRuntimeFiles(string name, bool expected)
    {
        VaultFiles.IsTransient(name).Should().Be(expected);
    }

    [Fact]
    public void IsLockedByNode_IsTrueOnlyWhileTheLockIsHeld()
    {
        VaultFiles.IsLockedByNode(_root).Should().BeFalse("no lock file at all");

        var lockPath = Path.Combine(_root, "node.lock");
        File.WriteAllText(lockPath, "");
        VaultFiles.IsLockedByNode(_root).Should().BeFalse("a leftover lock file nobody holds");

        using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            VaultFiles.IsLockedByNode(_root).Should().BeTrue();
        }
    }
}
