using System.Runtime.Versioning;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Tests.MacOs;

/// <summary>Runs only where it is compiled: in a macOS build of the test project (the file is removed from every other one).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsDesktopPlatformTests
{
    private static MacOsDesktopPlatform NewPlatform(string folder) => new(folder, new InMemorySecrets());

    [Fact]
    public void ThePlatform_SpeaksOfTheMenuBar_UsesTheTemplateIcon_AndKeepsItsLogOutOfAScratchRun()
    {
        var platform = NewPlatform(TestFolders.New("mac-platform"));

        platform.Name.Should().Be("macOS");
        platform.StatusAreaName.Should().Be("menu bar");
        platform.TrayIconAsset.Should().Be("avares://BeeMemoryBank.BlindDesktop/Assets/tray-template@2x.png");
        platform.TrayIconIsTemplate.Should().BeTrue("the menu bar tints a template image; without the flag macOS draws it as a plain black shape");
        platform.ErrorLogPath.Should().BeNull("a scratch run keeps the default file in the temp folder, not the user's log folder");
    }

    [Fact]
    public void TheDataFolder_IsExactlyTheOneGiven_AndPrivate()
    {
        var folder = Path.Combine(TestFolders.New("mac-platform"), "BeeMemoryBankBlind");

        var platform = NewPlatform(folder);

        platform.Paths.DataDirectory.Should().Be(folder);
        platform.Paths.DatabasePath.Should().StartWith(folder);
        File.GetUnixFileMode(folder).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void EverySeam_IsTheMacOsOne()
    {
        var platform = NewPlatform(TestFolders.New("mac-platform"));
        var services = new ServiceCollection();

        platform.AddSeams(services);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBlindPaths>().Should().BeOfType<MacOsBlindPaths>();
        provider.GetRequiredService<IBlindStateStore>().Should().BeOfType<MacOsBlindStateStore>();
        provider.GetRequiredService<IBlindAutostart>().Should().BeOfType<MacOsBlindAutostart>();
        provider.GetRequiredService<IBlindSecretStore>().Should().BeOfType<InMemorySecrets>("the test's own store replaced the Keychain one");
    }

    [Fact]
    public void ARealRunWithoutAReplacement_RegistersTheKeychainStore_WithoutOpeningIt()
    {
        var platform = new MacOsDesktopPlatform(TestFolders.New("mac-platform"));
        var services = new ServiceCollection();

        platform.AddSeams(services);
        using var provider = services.BuildServiceProvider();

        // Resolving builds the adapter object; no Keychain call is made until a key is read or written.
        provider.GetRequiredService<IBlindSecretStore>().Should().BeOfType<MacOsKeychainSecretStore>();
    }

    [Fact]
    public void AScratchFolder_GetsItsOwnKeychainServiceAndLoginItemLabel_NeverTheRealAppsOwn()
    {
        var one = TestFolders.New("mac-platform");
        var two = TestFolders.New("mac-platform");

        string LabelOf(string folder)
        {
            var services = new ServiceCollection();
            NewPlatform(folder).AddSeams(services);
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<MacOsBlindAutostart>().Label;
        }

        var labelOne = LabelOf(one);
        var labelTwo = LabelOf(two);

        labelOne.Should().StartWith(MacOsBlindAutostartOptions.DefaultLabel + ".").And.NotBe(MacOsBlindAutostartOptions.DefaultLabel);
        labelOne.Should().Be(LabelOf(one), "the same folder always maps to the same name");
        labelTwo.Should().NotBe(labelOne, "two scratch folders do not share a login item");
        labelOne.Should().EndWith(MacOsDesktopPlatform.FolderKey(one));

        var details = NewPlatform(one).SelfCheck().Single(i => i.Name == "login item").Detail;
        details.Should().Contain(labelOne);
    }

    [Fact]
    public void TheOneCopyRule_IsAFileLock_AndASecondStartIsRefusedAndAsksForTheWindow()
    {
        var folder = TestFolders.New("mac-platform");
        var platform = NewPlatform(folder);
        var other = NewPlatform(folder);

        using var first = platform.TryAcquireInstance();
        using var shown = new ManualResetEventSlim();
        first!.Listen(shown.Set);

        other.TryAcquireInstance().Should().BeNull("the folder already has a running copy");
        other.SignalRunningInstance().Should().BeTrue();
        shown.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        File.Exists(Path.Combine(folder, FileLockSingleInstance.LockFileName)).Should().BeTrue();
    }

    [Fact]
    public void TheCopyOfAnotherFolder_IsNotInTheWay()
    {
        using var first = NewPlatform(TestFolders.New("mac-platform")).TryAcquireInstance();
        using var second = NewPlatform(TestFolders.New("mac-platform")).TryAcquireInstance();

        first.Should().NotBeNull();
        second.Should().NotBeNull();
    }

    [Fact]
    public void ThePlatformsOwnSelfCheck_IsAllGreen_InAScratchFolder()
    {
        var items = NewPlatform(TestFolders.New("mac-platform")).SelfCheck();

        items.Should().OnlyContain(i => i.Ok, string.Join("; ", items.Where(i => !i.Ok).Select(i => i.Name + ": " + i.Detail)));
        items.Select(i => i.Name).Should().Contain(["private folder", "activation socket", "seam IBlindPaths", "seam IBlindStateStore",
            "seam IBlindAutostart", "login item"]);
    }

    [Fact]
    public void TheSelfCheck_NamesAWrongFolderMode()
    {
        var folder = TestFolders.New("mac-platform");
        var platform = NewPlatform(folder);
        _ = platform.Paths; // the folder is made private when the paths are first used; only then can it be loosened
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var item = platform.SelfCheck().Single(i => i.Name == "private folder");

        item.Ok.Should().BeFalse("a folder others can read is not private");
        item.Detail.Should().Contain("expected 700");
    }

    [Fact]
    public void TheFolderKey_IsShort_Stable_AndDependsOnTheFullPath()
    {
        var a = TestFolders.New("mac-platform");

        MacOsDesktopPlatform.FolderKey(a).Should().HaveLength(8).And.MatchRegex("^[0-9a-f]{8}$");
        MacOsDesktopPlatform.FolderKey(a).Should().Be(MacOsDesktopPlatform.FolderKey(a + "/"), "a trailing separator is the same folder");
        MacOsDesktopPlatform.FolderKey(a).Should().NotBe(MacOsDesktopPlatform.FolderKey(TestFolders.New("mac-platform")));
    }

    /// <summary>A key store in memory, so that the host's tests never touch a keychain.</summary>
    private sealed class InMemorySecrets : IBlindSecretStore
    {
        private byte[]? _seed, _backup, _pairing;
        public byte[]? LoadIdentitySeed() => _seed;
        public void SaveIdentitySeed(byte[] seed) => _seed = seed;
        public void SaveBackupKey(byte[] key) => _backup = key;
        public byte[]? LoadBackupKey() => _backup;
        public void SavePairingSecret(byte[] secret) => _pairing = secret;
        public byte[]? LoadPairingSecret() => _pairing;
        public void ClearPairingSecret() => _pairing = null;
        public void Clear() => _seed = _backup = _pairing = null;
    }
}
