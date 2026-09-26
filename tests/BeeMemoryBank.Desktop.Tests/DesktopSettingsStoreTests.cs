using System;
using System.IO;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

public sealed class DesktopSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-settings-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "desktop-settings.json");

    public DesktopSettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var store = new DesktopSettingsStore(SettingsPath);

        store.GetBool("preventSleep", defaultValue: false).Should().BeFalse();
        store.GetBool("autoCheckUpdates", defaultValue: true).Should().BeTrue();
    }

    [Fact]
    public void WritingOneKey_KeepsTheOthers()
    {
        // The old PreventSleepService rewrote the whole file with only its own key, which
        // would have wiped every other desktop setting.
        File.WriteAllText(SettingsPath, "{ \"preventSleep\": true, \"somethingElse\": 42 }");
        var store = new DesktopSettingsStore(SettingsPath);

        store.SetBool("autoCheckUpdates", false);

        var reread = new DesktopSettingsStore(SettingsPath);
        reread.GetBool("preventSleep", false).Should().BeTrue();
        reread.GetBool("autoCheckUpdates", true).Should().BeFalse();
        File.ReadAllText(SettingsPath).Should().Contain("somethingElse");
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndIsRepairedOnWrite()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var store = new DesktopSettingsStore(SettingsPath);

        store.GetBool("preventSleep", defaultValue: false).Should().BeFalse();
        store.SetBool("preventSleep", true);
        new DesktopSettingsStore(SettingsPath).GetBool("preventSleep", false).Should().BeTrue();
    }

    [Fact]
    public void ValidateAddExisting_RequiresAFolder()
    {
        StorageInputValidator.ValidateAddExisting("Work", "").IsValid.Should().BeFalse();
        StorageInputValidator.ValidateAddExisting("Work", "relative\\path").IsValid.Should().BeFalse();

        var ok = StorageInputValidator.ValidateAddExisting(" Work ", _dir);
        ok.IsValid.Should().BeTrue();
        ok.Name.Should().Be("Work");
        ok.ExplicitDataPath.Should().Be(_dir);
    }
}
