using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Profiles.Tests;

public sealed class ProfileServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _profilesFilePath;
    private readonly string _defaultVaultDir;
    private readonly string _vaultsParentDir;

    public ProfileServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "BmbProfileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _profilesFilePath = Path.Combine(_tempDirectory, "profiles.json");
        _defaultVaultDir = Path.Combine(_tempDirectory, "vaults", "default");
        _vaultsParentDir = Path.Combine(_tempDirectory, "vaults");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void FirstRun_CreatesDefaultRegistry_WithDefaultProfile()
    {
        // Arrange & Act
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);

        // Assert
        File.Exists(_profilesFilePath).Should().BeTrue();
        
        var profiles = service.GetAll();
        profiles.Should().HaveCount(1);
        profiles[0].Id.Should().Be("default");
        profiles[0].Name.Should().Be("Личный");
        profiles[0].DataPath.Should().Be(_defaultVaultDir);
        service.LastUsedProfileId.Should().Be("default");
        service.AutostartMode.Should().Be(AutostartMode.LastUsed);
        service.AutostartProfileId.Should().BeNull();
    }

    [Fact]
    public void CorruptedJson_FallsBackToBak_IfBakIsValid()
    {
        // Arrange
        // 1. Create a valid backup registry content
        var backupRegistry = new ProfilesRegistry
        {
            SchemaVersion = 1,
            LastUsedProfileId = "backup-id",
            AutostartMode = AutostartMode.FixedProfile,
            AutostartProfileId = "backup-id",
            Profiles = new List<ProfileEntry>
            {
                new ProfileEntry
                {
                    Id = "backup-id",
                    Name = "Backup Profile",
                    DataPath = Path.Combine(_vaultsParentDir, "backup-id"),
                    CreatedAt = DateTime.UtcNow,
                    LastUsedAt = DateTime.UtcNow
                }
            }
        };
        string backupJson = JsonSerializer.Serialize(backupRegistry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string bakPath = _profilesFilePath + ".bak";
        
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(bakPath, backupJson);

        // 2. Create corrupted main profiles.json
        File.WriteAllText(_profilesFilePath, "{ corrupted json... ## ");

        // Act
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);

        // Assert
        service.LastUsedProfileId.Should().Be("backup-id");
        service.AutostartMode.Should().Be(AutostartMode.FixedProfile);
        service.AutostartProfileId.Should().Be("backup-id");
        
        var profiles = service.GetAll();
        profiles.Should().HaveCount(1);
        profiles[0].Id.Should().Be("backup-id");
        profiles[0].Name.Should().Be("Backup Profile");

        // The main file should have been repaired with backup content
        File.Exists(_profilesFilePath).Should().BeTrue();
        string repairedContent = File.ReadAllText(_profilesFilePath);
        repairedContent.Should().Contain("backup-id");
        repairedContent.Should().NotContain("corrupted json");
    }

    [Fact]
    public void CorruptedJson_AndCorruptedOrMissingBak_RecreatesDefaultRegistryWithoutException()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(_profilesFilePath, "invalid json");
        File.WriteAllText(_profilesFilePath + ".bak", "{ also invalid }");

        // Act
        Action act = () => _ = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);

        // Assert
        act.Should().NotThrow();
        
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        service.GetAll().Should().ContainSingle(p => p.Id == "default");
    }

    [Fact]
    public void BakReflectsPreviousSuccessfulStateAfterSeveralSaves()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        string bakPath = _profilesFilePath + ".bak";

        // Assert initially bak doesn't exist since we haven't saved over an existing valid file yet
        File.Exists(bakPath).Should().BeFalse();

        // Act 1: Add first profile (triggers save)
        var profile1 = service.AddProfile("Profile 1");
        
        // Assert: bak should now exist and contain the state *before* adding Profile 1 (which only had default)
        File.Exists(bakPath).Should().BeTrue();
        string bakContent1 = File.ReadAllText(bakPath);
        bakContent1.Should().Contain("default");
        bakContent1.Should().NotContain(profile1.Id);

        // Act 2: Add second profile (triggers save)
        var profile2 = service.AddProfile("Profile 2");

        // Assert: bak should contain default and Profile 1, but not Profile 2
        string bakContent2 = File.ReadAllText(bakPath);
        bakContent2.Should().Contain("default");
        bakContent2.Should().Contain(profile1.Id);
        bakContent2.Should().NotContain(profile2.Id);
    }

    [Fact]
    public void AddProfile_GeneratesValidId_AndUpdatesRegistry()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        int initialCount = service.GetAll().Count;

        // Act
        var added = service.AddProfile("New Profile");

        // Assert
        added.Should().NotBeNull();
        added.Id.Should().NotBeNullOrWhiteSpace().And.HaveLength(8);
        added.Name.Should().Be("New Profile");
        added.DataPath.Should().Be(Path.Combine(_vaultsParentDir, added.Id));
        added.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        added.LastUsedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        var all = service.GetAll();
        all.Should().HaveCount(initialCount + 1);
        all.Should().ContainSingle(p => p.Id == added.Id);
    }

    [Fact]
    public void AddProfile_WithExplicitDataPath_AcceptsStandardAndNonStandardPaths()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        string customPath = Path.Combine(Path.GetTempPath(), "CustomBmbVaultLocation_" + Guid.NewGuid().ToString("N"));

        // Act
        var added = service.AddProfile("Custom Location Vault", customPath);

        // Assert
        added.DataPath.Should().Be(customPath);
        service.GetById(added.Id).DataPath.Should().Be(customPath);
    }

    [Fact]
    public void AddProfile_ThrowsOnInvalidInputs()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);

        // Act & Assert
        Action actEmptyName = () => service.AddProfile("");
        actEmptyName.Should().Throw<ArgumentException>();

        Action actRelativePath = () => service.AddProfile("Test", "relative/path");
        actRelativePath.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RenameProfile_ModifiesNameOnly_AndSaves()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        var added = service.AddProfile("Original Name");

        // Act
        service.RenameProfile(added.Id, "New Name");

        // Assert
        var updated = service.GetById(added.Id);
        updated.Name.Should().Be("New Name");
        updated.DataPath.Should().Be(added.DataPath); // Unchanged
    }

    [Fact]
    public void RenameProfile_ThrowsIfIdNotFoundOrNameInvalid()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);

        // Act & Assert
        Action actNotFound = () => service.RenameProfile("nonexistent", "New Name");
        actNotFound.Should().Throw<KeyNotFoundException>();

        Action actEmptyName = () => service.RenameProfile("default", "");
        actEmptyName.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForgetProfile_RemovesFromList_DoesNotDeleteFiles()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        var added = service.AddProfile("Temp Profile");
        string path = added.DataPath;
        
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "test.txt"), "some content");

        // Act
        service.ForgetProfile(added.Id);

        // Assert
        service.GetAll().Should().NotContain(p => p.Id == added.Id);
        
        // Ensure files on disk are completely untouched
        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "test.txt")).Should().BeTrue();
    }

    [Fact]
    public void ForgetProfile_ThrowsIfLastProfile()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        
        // Assert initially only has default
        service.GetAll().Should().HaveCount(1);

        // Act & Assert
        Action act = () => service.ForgetProfile("default");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ForgetProfile_FallsBackLastUsedAndAutostart()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        var extra = service.AddProfile("Extra");
        
        service.SetLastUsed(extra.Id);
        service.SetAutostart(AutostartMode.FixedProfile, extra.Id);

        service.LastUsedProfileId.Should().Be(extra.Id);
        service.AutostartProfileId.Should().Be(extra.Id);

        // Act
        service.ForgetProfile(extra.Id);

        // Assert
        service.LastUsedProfileId.Should().Be("default"); // Fell back to first remaining
        service.AutostartMode.Should().Be(AutostartMode.LastUsed); // Reset autostart mode
        service.AutostartProfileId.Should().BeNull(); // Reset fixed profile ID
    }

    [Fact]
    public void SetLastUsed_UpdatesValuesAndSaves()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        var extra = service.AddProfile("Extra");

        // Act
        service.SetLastUsed(extra.Id);

        // Assert
        service.LastUsedProfileId.Should().Be(extra.Id);
        var updated = service.GetById(extra.Id);
        updated.LastUsedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SetAutostart_ValidatesAndSaves()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        var extra = service.AddProfile("Extra");

        // Act 1: Set fixed autostart
        service.SetAutostart(AutostartMode.FixedProfile, extra.Id);

        // Assert
        service.AutostartMode.Should().Be(AutostartMode.FixedProfile);
        service.AutostartProfileId.Should().Be(extra.Id);

        // Act 2: Set last used autostart
        service.SetAutostart(AutostartMode.LastUsed);

        // Assert
        service.AutostartMode.Should().Be(AutostartMode.LastUsed);
        service.AutostartProfileId.Should().BeNull();

        // Act 3: Try set fixed with invalid ID
        Action actInvalid = () => service.SetAutostart(AutostartMode.FixedProfile, "invalid-id");
        actInvalid.Should().Throw<ArgumentException>();

        // Act 4: Try set fixed with null ID
        Action actNull = () => service.SetAutostart(AutostartMode.FixedProfile, null);
        actNull.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetLastUsedOrDefault_ReturnsExpectedFallbacks()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        
        // Assert initial
        service.GetLastUsedOrDefault().Id.Should().Be("default");

        // Set last used
        var extra = service.AddProfile("Extra");
        service.SetLastUsed(extra.Id);
        service.GetLastUsedOrDefault().Id.Should().Be(extra.Id);

        // Mutate registry file directly to break LastUsedProfileId pointer
        string content = File.ReadAllText(_profilesFilePath);
        content = content.Replace($"\"lastUsedProfileId\": \"{extra.Id}\"", "\"lastUsedProfileId\": \"corrupted-pointer-id\"");
        File.WriteAllText(_profilesFilePath, content);

        // Reload service to read corrupted pointer
        var service2 = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        
        // GetLastUsedOrDefault should fall back to the first profile in the list (which is "default")
        service2.GetLastUsedOrDefault().Id.Should().Be("default");
    }

    [Fact]
    public void FileAndModels_DoNotContainAnySecrets()
    {
        // 1. Inspect file contents directly
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        service.AddProfile("Normal Profile Name");
        
        string content = File.ReadAllText(_profilesFilePath);
        var forbiddenWords = new[] { "password", "secret", "token", "key", "dek", "cek", "encrypt", "pass", "pwd" };
        foreach (var word in forbiddenWords)
        {
            content.Should().NotContainEquivalentOf(word);
        }

        // 2. Inspect classes ProfileEntry and ProfilesRegistry properties
        var types = new[] { typeof(ProfileEntry), typeof(ProfilesRegistry), typeof(AutostartMode) };
        foreach (var type in types)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var word in forbiddenWords)
                {
                    prop.Name.Should().NotContainEquivalentOf(word);
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentSaveCalls_DoNotCorruptFile()
    {
        // Arrange
        var service = new ProfileService(_profilesFilePath, _defaultVaultDir, _vaultsParentDir);
        int initialCount = service.GetAll().Count;
        int tasksCount = 10;
        int modificationsPerTask = 20;

        // Act
        var tasks = Enumerable.Range(0, tasksCount).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < modificationsPerTask; i++)
            {
                service.AddProfile($"Thread-{t}-Profile-{i}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var all = service.GetAll();
        all.Count.Should().Be(initialCount + (tasksCount * modificationsPerTask));

        // Read and deserialize file to verify it's perfectly valid
        var deserialized = JsonSerializer.Deserialize<ProfilesRegistry>(
            File.ReadAllBytes(_profilesFilePath),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            });
        
        deserialized.Should().NotBeNull();
        deserialized!.IsValid().Should().BeTrue();
        deserialized.Profiles.Should().HaveCount(all.Count);
    }
}
