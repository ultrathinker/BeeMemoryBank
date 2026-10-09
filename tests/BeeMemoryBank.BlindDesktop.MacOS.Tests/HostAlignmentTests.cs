using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>
/// Stage 3b, alignment with what the shared host and Blind.AppCore expect of an adapter since the Windows stage: the state store's
/// <c>Erase()</c>, the autostart's three-valued <c>IsEnabled</c>, the "cannot tell" flags of the device state, and the controller's
/// <c>KeyStoreUnavailable</c> proven with the real controller over the Keychain store.
/// </summary>
public class StateStoreEraseTests
{
    [Fact]
    public void Erase_ThroughTheSeam_RemovesTheStateFileAndItsDamagedCopies()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var store = new MacOsBlindStateStore(paths);
        store.Set("bmb.blind.desktop.obsolete_host_state", "x");
        File.WriteAllText(Path.Combine(paths.DataDirectory, "blind-state.json.damaged-1"), "old");
        File.WriteAllText(Path.Combine(paths.DataDirectory, "keep.txt"), "mine");
        IBlindStateStore seam = store;

        seam.Erase();

        Directory.GetFiles(paths.DataDirectory, "blind-state.json*").Should().BeEmpty();
        File.Exists(Path.Combine(paths.DataDirectory, "keep.txt")).Should().BeTrue();
        store.Get("bmb.blind.desktop.obsolete_host_state").Should().BeNull();
    }

    [Fact]
    public void TheCoresWipe_CallsErase_AfterClearingTheValues()
    {
        using var root = new TempFolder();
        var paths = new MacOsBlindPaths(root.Path);
        var store = new MacOsBlindStateStore(paths);
        var state = new BlindPhoneState(store) { DisplayName = "Test Mac", Schedule = BlindBackupSchedule.Daily };
        File.Exists(store.FilePath).Should().BeTrue();

        // what BlindPhoneReset does: every key to null, then Erase
        state.Clear();
        ((IBlindStateStore)store).Erase();

        File.Exists(store.FilePath).Should().BeFalse("the file is removed, not left empty");
        new BlindPhoneState(new MacOsBlindStateStore(paths)).DisplayName.Should().BeNull();
    }
}

public class AtDirectoryPathsTests
{
    [Fact]
    public void AtDirectory_IsExactlyThatFolder_WithTheDatabaseInIt_AndMode0700()
    {
        using var root = new TempFolder();
        var scratch = Path.Combine(root.Path, "scratch-data");

        var paths = MacOsBlindPaths.AtDirectory(scratch);

        paths.DataDirectory.Should().Be(scratch);
        paths.DatabasePath.Should().Be(Path.Combine(scratch, "beememorybank.db"));
        Directory.Exists(scratch).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(scratch).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void TheHostOptions_DataDirectory_WinsOverTheApplicationSupportRoot()
    {
        using var root = new TempFolder();
        var exact = Path.Combine(root.Path, "exact");

        var options = new MacOsBlindHostOptions { ApplicationSupportRoot = Path.Combine(root.Path, "support"), DataDirectory = exact };

        options.Paths.DataDirectory.Should().Be(exact);
        Directory.Exists(Path.Combine(root.Path, "support")).Should().BeFalse();
    }

    [Fact]
    public void ABlankFolder_IsRefused()
    {
        var act = () => MacOsBlindPaths.AtDirectory(" ", createDirectory: false);
        act.Should().Throw<ArgumentException>();
    }
}

public class AutostartIsEnabledTests
{
    private const string Label = "test.beememorybank.blind.isenabled";

    private static MacOsBlindAutostart Make(TempFolder folder) => new(
        new MacOsBlindAutostartOptions { Label = Label, LaunchAgentsDirectory = folder.File("LaunchAgents"), ProgramArguments = ["/usr/bin/true", "--minimized"], LoadImmediately = false },
        new FakeCommandRunner(), () => 501u, _ => null, fileExists: _ => true);

    [Fact]
    public void ThroughTheSeam_NoPlist_IsFalse_AndEnabledIsTrue()
    {
        using var folder = new TempFolder();
        var autostart = Make(folder);
        IBlindAutostart seam = autostart;

        seam.IsEnabled.Should().BeFalse("there is no login item");
        seam.SetEnabled(true);
        seam.IsEnabled.Should().BeTrue();
        seam.SetEnabled(false);
        seam.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void APlistThatIsNotOurs_Or_Garbage_IsFalse_NotUnknown()
    {
        using var folder = new TempFolder();
        var autostart = Make(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(autostart.PlistPath)!);

        File.WriteAllText(autostart.PlistPath, LaunchAgentPlist.Build("someone.elses.label", ["/bin/true"]));
        autostart.IsEnabled.Should().BeFalse();

        File.WriteAllText(autostart.PlistPath, "garbage");
        autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void APlistThatCannotBeRead_IsUnknown_NotFalse()
    {
        using var folder = new TempFolder();
        var autostart = Make(folder);
        autostart.SetEnabled(true);

        // another process holds the file exclusively (a sharing violation on Windows; the advisory lock .NET takes on Unix): it cannot be read now
        using var hold = new FileStream(autostart.PlistPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        autostart.IsEnabled.Should().BeNull("the screen shows the toggle as unknown instead of guessing");
        ((IBlindAutostart)autostart).IsEnabled.Should().BeNull();
    }
}

/// <summary>
/// <see cref="BlindAppStatus.KeyStoreUnavailable"/> with the real <see cref="BlindAppController"/> over the real Keychain secret store (the
/// Keychain itself is the in-memory fake, so it can be locked and unlocked at will): a locked or denying Keychain makes the store throw a
/// <see cref="BlindSecretStoreException"/> (an <see cref="IOException"/>), which must show as "the key store did not answer", never as a
/// lost key and never with a secret in it.
/// </summary>
public class KeyStoreUnavailableTests
{
    private static ServiceProvider Build(TempFolder root, FakeKeychainBackend keychain)
    {
        var services = new ServiceCollection();
        services.AddSeamsAndCore(new MacOsBlindHostOptions
        {
            ApplicationSupportRoot = root.Path,
            KeychainBackend = keychain,
            LaunchAgentsDirectory = root.File("LaunchAgents"),
            AutostartProgramArguments = ["/usr/bin/true"],
            LoadAutostartImmediately = false,
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task ALockedKeychain_ShowsAsKeyStoreUnavailable_NotAsALostKey_AndComesBack()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        var app = provider.GetRequiredService<IBlindAppController>();
        await app.InitializeAsync();
        var seed = provider.GetRequiredService<IBlindSecretStore>().LoadIdentitySeed()!;
        var before = app.GetStatus();
        before.KeyStoreUnavailable.Should().BeNull();
        before.AwaitingAnswer.Should().BeTrue();
        before.BackupKeyLost.Should().BeFalse();
        app.PairingCode().Should().NotBeNull();

        keychain.Locked = true;
        var locked = app.GetStatus();

        locked.KeyStoreUnavailable.Should().NotBeNull().And.Contain("BlindSecretStoreException").And.Contain("-25308");
        locked.KeyStoreUnavailable!.Length.Should().BeLessThan(160);
        locked.KeyStoreUnavailable.Should().NotContain(Convert.ToHexString(seed)).And.NotContain(Convert.ToBase64String(seed));
        locked.BackupKeyLost.Should().BeFalse("a Keychain that does not answer is not a lost key");
        locked.AwaitingAnswer.Should().BeFalse("not known, so not claimed");
        locked.StartError.Should().BeNull();
        app.PairingCode().Should().BeNull();
        locked.NodeId.Should().Be(before.NodeId, "nothing was changed");

        keychain.Locked = false;
        var back = app.GetStatus();
        back.KeyStoreUnavailable.Should().BeNull();
        back.AwaitingAnswer.Should().BeTrue();
        back.BackupKeyLost.Should().BeFalse();
        app.PairingCode().Should().StartWith("bmb-blind-phone:?");
        keychain.Items.Should().HaveCount(3, "nothing was removed or made up while the Keychain was locked");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ADeniedRead_OfOneItem_IsReportedTheSameWay()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        var app = provider.GetRequiredService<IBlindAppController>();
        await app.InitializeAsync();

        keychain.FailNext["copy"] = KeychainStatus.AuthFailed;
        var status = app.GetStatus();

        status.KeyStoreUnavailable.Should().Contain("-25293");
        status.BackupKeyLost.Should().BeFalse();
        app.GetStatus().KeyStoreUnavailable.Should().BeNull("one denied call is not a lasting state");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ATamperedItem_IsReportedWithTheAdvice_WithinWhatTheScreenShows()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        var app = provider.GetRequiredService<IBlindAppController>();
        await app.InitializeAsync();
        keychain.Put("com.beememorybank.blind", "backup-key-v1", new byte[64]);   // not an envelope of ours

        var status = app.GetStatus();

        status.KeyStoreUnavailable.Should().Contain("BlindSecretStoreException").And.Contain("altered outside the app").And.Contain("wipe",
            "the advice survives AppCore's cut to one short line");
        status.BackupKeyLost.Should().BeFalse();
        keychain.Items[("com.beememorybank.blind", "backup-key-v1")].Should().Equal(new byte[64], "the tampered item is left as it is, no new key is made over it");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task TheKeychainStoreThrows_AnIOException_TheKindAppCoreCatchesForTheStatus()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        using var provider = Build(root, keychain);
        await provider.GetRequiredService<IBlindAppController>().InitializeAsync();
        keychain.Locked = true;

        var act = () => provider.GetRequiredService<IBlindSecretStore>().LoadBackupKey();

        act.Should().Throw<BlindSecretStoreException>().Which.Should().BeAssignableTo<IOException>();
        SqliteConnection.ClearAllPools();
    }
}
