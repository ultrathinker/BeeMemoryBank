using System;
using System.IO;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Profiles;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// Covers <see cref="AutostartProfileResolver"/> per _СУПЕРПЛАН-МУЛЬТИАККАУНТ.md §4.6:
/// FixedProfile pins a profile; LastUsed (the default) tracks lastUsedProfileId; a stale/missing
/// pinned profile falls back to lastUsed/default rather than crashing app startup.
/// </summary>
public sealed class AutostartProfileResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileService _profiles;

    public AutostartProfileResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bmb-autostart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _profiles = new ProfileService(
            Path.Combine(_tempDir, "profiles.json"),
            defaultVaultDir: Path.Combine(_tempDir, "vault-default"),
            vaultsParentDir: _tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void LastUsedMode_ResolvesToLastUsedProfile()
    {
        var b = _profiles.AddProfile("B");
        _profiles.SetLastUsed(b.Id);

        var resolved = AutostartProfileResolver.Resolve(_profiles);

        resolved.Id.Should().Be(b.Id);
    }

    [Fact]
    public void FixedProfileMode_ResolvesToPinnedProfile_EvenIfNotLastUsed()
    {
        var a = _profiles.GetAll()[0];
        var b = _profiles.AddProfile("B");
        _profiles.SetLastUsed(b.Id); // last used is B...
        _profiles.SetAutostart(AutostartMode.FixedProfile, a.Id); // ...but autostart pins A

        var resolved = AutostartProfileResolver.Resolve(_profiles);

        resolved.Id.Should().Be(a.Id, "FixedProfile must win over lastUsed");
    }

    [Fact]
    public void FixedProfileMode_StalePinnedId_FallsBackToLastUsed()
    {
        var b = _profiles.AddProfile("B");
        _profiles.SetLastUsed(b.Id);
        _profiles.SetAutostart(AutostartMode.FixedProfile, b.Id);
        _profiles.ForgetProfile(b.Id); // pinned profile no longer exists

        var resolved = AutostartProfileResolver.Resolve(_profiles);

        resolved.Should().NotBeNull("a broken autostart pin must never prevent startup");
    }

    [Fact]
    public void SingleProfileInstallation_ResolvesToDefault_BehaviorUnchanged()
    {
        var resolved = AutostartProfileResolver.Resolve(_profiles);

        resolved.Id.Should().Be("default");
    }
}
