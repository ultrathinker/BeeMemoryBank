using System;
using System.IO;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Profiles;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// Covers the pure UI-logic helpers extracted for Этап 5:
/// <list type="bullet">
/// <item><see cref="StorageDisplayLogic.FormatShellTitle"/> — the §4.5 "show profile name only
/// when ≥ 2 profiles" rule that drives MainWindow.Title and the tray tooltip.</item>
/// <item><see cref="StorageInputValidator.ValidateCreate"/> / <see cref="StorageInputValidator.ValidateRename"/>
/// — create/rename dialog input validation that runs before ProfileService is touched.</item>
/// </list>
/// These are the only pieces of Этап 5 that are not Avalonia-UI-bound; everything else
/// (tray submenu, dialogs, manage window) is untestable without a headable Avalonia host,
/// which the brief explicitly excludes from the testing scope.
/// </summary>
public sealed class StorageDisplayLogicTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileService _profiles;

    public StorageDisplayLogicTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bmb-uxlogic-" + Guid.NewGuid().ToString("N"));
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

    // ── FormatShellTitle: count/name primitives ─────────────────────────────────

    [Theory]
    [InlineData(0, null, "BeeMemoryBank")]
    [InlineData(1, null, "BeeMemoryBank")]
    [InlineData(1, "Личный", "BeeMemoryBank")]
    [InlineData(2, null, "BeeMemoryBank")]
    [InlineData(2, "", "BeeMemoryBank")]
    [InlineData(2, "   ", "BeeMemoryBank")]
    [InlineData(2, "Рабочий", "BeeMemoryBank — Рабочий")]
    [InlineData(5, "B", "BeeMemoryBank — B")]
    public void FormatShellTitle_FollowsCountRule(int count, string? name, string expected)
    {
        StorageDisplayLogic.FormatShellTitle(count, name).Should().Be(expected);
    }

    // ── FormatShellTitle: ProfileService overload ───────────────────────────────

    [Fact]
    public void FormatShellTitle_SingleProfile_BareProductName()
    {
        // Constructor seeds one default profile.
        _profiles.GetAll().Count.Should().Be(1);

        StorageDisplayLogic.FormatShellTitle(_profiles, activeProfileId: _profiles.GetAll()[0].Id)
            .Should().Be("BeeMemoryBank", "single-profile install must be indistinguishable from today");
    }

    [Fact]
    public void FormatShellTitle_TwoProfiles_ActiveNameAppended()
    {
        var a = _profiles.GetAll()[0];
        var b = _profiles.AddProfile("B", Path.Combine(_tempDir, "vault-b"));

        StorageDisplayLogic.FormatShellTitle(_profiles, activeProfileId: b.Id)
            .Should().Be("BeeMemoryBank — B");
        StorageDisplayLogic.FormatShellTitle(_profiles, activeProfileId: a.Id)
            .Should().Be("BeeMemoryBank — Личный");
    }

    [Fact]
    public void FormatShellTitle_StaleActiveId_FallsBackToBareName()
    {
        _profiles.AddProfile("B", Path.Combine(_tempDir, "vault-b"));

        // Active id points to a profile that no longer exists — must NOT throw, must NOT
        // show a phantom name.
        var title = StorageDisplayLogic.FormatShellTitle(_profiles, activeProfileId: "no-such-id");
        title.Should().Be("BeeMemoryBank");
    }

    [Fact]
    public void FormatShellTitle_NullProfiles_BareProductName_NoThrow()
    {
        StorageDisplayLogic.FormatShellTitle(null!, activeProfileId: "any")
            .Should().Be("BeeMemoryBank");
    }

    [Fact]
    public void FormatShellTitle_NullActiveId_EvenWithTwoProfiles_BareName()
    {
        _profiles.AddProfile("B", Path.Combine(_tempDir, "vault-b"));
        StorageDisplayLogic.FormatShellTitle(_profiles, activeProfileId: null)
            .Should().Be("BeeMemoryBank", "no active profile yet → no name to show");
    }
}

/// <summary>
/// <see cref="StorageInputValidator"/> — input validation for the create / rename dialogs.
/// </summary>
public sealed class StorageInputValidatorTests : IDisposable
{
    private readonly string _tempDir;

    public StorageInputValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bmb-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── ValidateCreate ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_NullName_IsInvalid()
    {
        var r = StorageInputValidator.ValidateCreate(null, null);
        r.IsValid.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Create_EmptyOrWhitespaceName_IsInvalid(string name)
    {
        var r = StorageInputValidator.ValidateCreate(name, null);
        r.IsValid.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_ValidName_NoDataPath_TrimsAndAccepts()
    {
        var r = StorageInputValidator.ValidateCreate("  Рабочий  ", null);
        r.IsValid.Should().BeTrue();
        r.Name.Should().Be("Рабочий");
        r.ExplicitDataPath.Should().BeNull();
    }

    [Fact]
    public void Create_NameTooLong_IsInvalid()
    {
        var r = StorageInputValidator.ValidateCreate(new string('a', 101), null);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_NameExactly100_Ok()
    {
        var r = StorageInputValidator.ValidateCreate(new string('a', 100), null);
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_RelativeDataPath_IsInvalid()
    {
        var r = StorageInputValidator.ValidateCreate("Foo", "relative/path");
        r.IsValid.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_AbsoluteDataPath_Normalized()
    {
        var abs = Path.GetFullPath(Path.Combine(_tempDir, "my-vault"));
        var r = StorageInputValidator.ValidateCreate("Foo", $"  {abs}  ");
        r.IsValid.Should().BeTrue();
        r.Name.Should().Be("Foo");
        r.ExplicitDataPath.Should().Be(abs);
    }

    [Fact]
    public void Create_EmptyDataPath_TreatedAsNone()
    {
        var r = StorageInputValidator.ValidateCreate("Foo", "   ");
        r.IsValid.Should().BeTrue();
        r.ExplicitDataPath.Should().BeNull();
    }

    // ── ValidateRename ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_Empty_IsInvalid(string? name)
    {
        var r = StorageInputValidator.ValidateRename(name);
        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rename_Valid_Trims()
    {
        var r = StorageInputValidator.ValidateRename("  Новое  ");
        r.IsValid.Should().BeTrue();
        r.Name.Should().Be("Новое");
    }

    [Fact]
    public void Rename_TooLong_IsInvalid()
    {
        var r = StorageInputValidator.ValidateRename(new string('a', 101));
        r.IsValid.Should().BeFalse();
    }
}
