using System;
using System.IO;
using BeeMemoryBank.AppPaths;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.AppPaths.Tests;

public class BmbPathsTests
{
    [Fact]
    public void Root_AndDependentPaths_AreNeverUnderBaseDirectory()
    {
        // Arrange
        string baseDir = Path.GetFullPath(AppContext.BaseDirectory);

        // Act
        string root = Path.GetFullPath(BmbPaths.Root);
        string profiles = Path.GetFullPath(BmbPaths.ProfilesFile);
        string settings = Path.GetFullPath(BmbPaths.DesktopSettingsFile);
        string logs = Path.GetFullPath(BmbPaths.LogsDir);
        string migrations = Path.GetFullPath(BmbPaths.MigrationDir);
        string vaults = Path.GetFullPath(BmbPaths.VaultsDir);
        string defaultVault = Path.GetFullPath(BmbPaths.DefaultVaultDir);

        // Assert
        // The root directory must not be equal to AppContext.BaseDirectory
        root.Should().NotBe(baseDir);
        
        // The root directory must not be nested inside AppContext.BaseDirectory
        root.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        root.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();

        // All dependent paths must not be nested under AppContext.BaseDirectory
        profiles.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        settings.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        logs.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        migrations.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        vaults.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        defaultVault.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("vault/..")]
    [InlineData("../vault")]
    [InlineData(@"vault\..")]
    [InlineData(@"..\vault")]
    [InlineData("vault/sub")]
    [InlineData(@"vault\sub")]
    [InlineData("vault:sub")]
    [InlineData("vault*")]
    [InlineData("vault?")]
    [InlineData("vault<")]
    [InlineData("vault>")]
    [InlineData("vault|")]
    [InlineData("vault\"")]
    [InlineData("/absolute/path")]
    [InlineData(@"C:\absolute\path")]
    public void VaultDir_RejectsInvalidOrPathTraversalVaultIds(string? invalidVaultId)
    {
        // Act & Assert
        // Rationale: Throwing an exception is selected instead of silently sanitizing/escaping
        // because vault directories hold critical user data and silent correction could lead to
        // unexpected behavior or data overlapping, which poses a security/integrity risk.
        Action act = () => BmbPaths.VaultDir(invalidVaultId!);
        
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void VaultDir_AcceptsValidVaultIds()
    {
        // Arrange
        string[] validIds = ["default", "user1", "my-vault-2", "vault_3"];

        // Act & Assert
        foreach (var id in validIds)
        {
            Action act = () => BmbPaths.VaultDir(id);
            act.Should().NotThrow();
            
            string path = BmbPaths.VaultDir(id);
            Directory.Exists(path).Should().BeTrue();
        }
    }

    [Fact]
    public void AllPaths_AreAbsolute()
    {
        // Act & Assert
        Path.IsPathRooted(BmbPaths.Root).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.ProfilesFile).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.DesktopSettingsFile).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.LogsDir).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.MigrationDir).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.VaultsDir).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.DefaultVaultDir).Should().BeTrue();
        Path.IsPathRooted(BmbPaths.VaultDir("user-vault")).Should().BeTrue();
    }

    [Fact]
    public void DefaultVaultDir_IsEquivalentToVaultDirWithDefault()
    {
        // Arrange & Act
        string defaultVaultDir = BmbPaths.DefaultVaultDir;
        string vaultDirWithDefault = BmbPaths.VaultDir("default");

        // Assert
        defaultVaultDir.Should().Be(vaultDirWithDefault);
    }

    [Fact]
    public void RepeatedCalls_AreIdempotent()
    {
        // Act & Assert
        Action act = () =>
        {
            // Accessing multiple times shouldn't throw or create duplicate directories
            _ = BmbPaths.Root;
            _ = BmbPaths.ProfilesFile;
            _ = BmbPaths.DesktopSettingsFile;
            _ = BmbPaths.LogsDir;
            _ = BmbPaths.MigrationDir;
            _ = BmbPaths.VaultsDir;
            _ = BmbPaths.DefaultVaultDir;
            _ = BmbPaths.VaultDir("idempotent-test-vault");
        };

        act.Should().NotThrow();
    }
}
