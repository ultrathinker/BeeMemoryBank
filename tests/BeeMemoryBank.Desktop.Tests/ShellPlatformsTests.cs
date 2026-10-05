using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The one place that picks the shell's operating-system services (autostart, sleep, Finder/Explorer, tray icon), and the Windows set
/// behind it. The shared code of the shell only ever asks <see cref="IShellPlatform"/>.
/// </summary>
public sealed class ShellPlatformsTests
{
    private readonly string _dir = TestScratch.New("shellplatform");

    private static SleepLockRequest NoLock => _ => Task.FromResult(new SleepLockResult(true));

    [Fact]
    public void TheRunningSystem_IsDetected_AndCurrentIsItsPlatform()
    {
        var expected = OperatingSystem.IsWindows() ? ShellOs.Windows : OperatingSystem.IsMacOS() ? ShellOs.MacOs : ShellOs.Other;

        ShellPlatforms.Detect().Should().Be(expected);
        ShellPlatforms.Current.Os.Should().Be(expected);
        ShellPlatforms.Current.Should().BeSameAs(ShellPlatforms.Current, "the platform is chosen once");
    }

    [Fact]
    public void Windows_IsTheSetTheShellHadBefore()
    {
        var platform = ShellPlatforms.For(ShellOs.Windows);

        platform.Os.Should().Be(ShellOs.Windows);
        platform.CreateAutostart().Should().BeOfType<AutostartService>();
        platform.FileManager.Should().BeOfType<WindowsFileManager>();
        platform.TrayIconAsset.Should().Be("avares://BeeMemoryBank.Desktop/Assets/icon.png");
        platform.TrayIconIsTemplate.Should().BeFalse();
        platform.QuitMenuText.Should().Be("Exit");
        platform.InterceptsApplicationQuit.Should().BeFalse("on Windows only the tray menu and the update restart end the app, and they already stop the node");
    }

    [Fact]
    public void TheWindowsTexts_AreTheOnesTheWindowsHadInTheirXaml()
    {
        // The XAML keeps the Windows wording as its default text; the code sets the platform's text over it. If the two drifted, Windows
        // would show something different from before.
        var platform = ShellPlatforms.For(ShellOs.Windows);

        File.ReadAllText(RepoFile("desktop", "BeeMemoryBank.Desktop", "Views", "SettingsWindow.axaml")).Should().Contain(platform.AutostartCheckText);
        File.ReadAllText(RepoFile("desktop", "BeeMemoryBank.Desktop", "Views", "ManageStoragesWindow.axaml")).Should().Contain(platform.AutostartProfileNote);
    }

    [WindowsOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_CreatesTheWindowsServices_WithoutStartingThem()
    {
        var platform = ShellPlatforms.For(ShellOs.Windows);

        var sleep = platform.CreatePreventSleep(new DesktopSettingsStore(Path.Combine(_dir, "settings.json")));
        sleep.Should().BeOfType<PreventSleepService>();
        sleep!.IsEnabled.Should().BeFalse("constructing the service applies nothing");

        using var power = platform.CreatePowerEvents(NoLock);
        power.Should().BeOfType<PowerEventsService>();
    }

    [Fact]
    public void MacOs_IsTheMacSet_AndCreatingItCallsNothingNative()
    {
        // built on every OS (the services only touch IOKit / launchd / osascript when they are used, and only on a Mac)
        var platform = ShellPlatforms.For(ShellOs.MacOs);

        platform.Os.Should().Be(ShellOs.MacOs);
        platform.CreateAutostart().Should().BeOfType<MacOS.MacOsAutostart>();
        platform.FileManager.Should().BeOfType<MacOS.MacOsFileManager>();
        using var sleep = platform.CreatePreventSleep(new DesktopSettingsStore(Path.Combine(_dir, "settings.json"))) as IDisposable;
        sleep.Should().BeOfType<MacOS.MacOsPreventSleep>();
        using var power = platform.CreatePowerEvents(NoLock);
        power.Should().BeOfType<MacOS.MacOsSleepMonitor>();
        platform.TrayIconIsTemplate.Should().BeTrue();
        platform.TrayIconAsset.Should().EndWith("tray-template@2x.png");
        platform.InterceptsApplicationQuit.Should().BeTrue("Cmd+Q must be caught and routed through the graceful stop");
        platform.QuitMenuText.Should().Contain("Quit");
        platform.AutostartCheckText.Should().NotContain("Windows");
        platform.AutostartProfileNote.Should().NotContain("Windows");
    }

    [Theory]
    [InlineData(ShellOs.Other)]
    public void ASystemWithoutShellServices_SaysSo_InsteadOfPretending(ShellOs os)
    {
        var platform = ShellPlatforms.For(os);

        platform.CreatePreventSleep(new DesktopSettingsStore(Path.Combine(_dir, "settings.json"))).Should().BeNull();
        platform.CreatePowerEvents(NoLock).Should().BeNull();

        var autostart = platform.CreateAutostart();
        autostart.IsEnabled.Should().BeFalse();
        var enable = () => autostart.Enable();
        enable.Should().Throw<PlatformNotSupportedException>();
        var disable = () => autostart.Disable();
        disable.Should().Throw<PlatformNotSupportedException>();

        // the file manager does nothing and does not throw
        platform.FileManager.OpenFolder(Path.Combine(_dir, "x"));
        platform.FileManager.Reveal(Path.Combine(_dir, "x"));
        Directory.Exists(Path.Combine(_dir, "x")).Should().BeFalse();
        platform.InterceptsApplicationQuit.Should().BeFalse();
    }

    // ── WindowsFileManager ──────────────────────────────────────────────────────

    private static (WindowsFileManager Manager, List<ProcessStartInfo> Started) ManagerWithFake(Action<ProcessStartInfo>? onStart = null)
    {
        var started = new List<ProcessStartInfo>();
        return (new WindowsFileManager(info => { started.Add(info); onStart?.Invoke(info); }), started);
    }

    [Fact]
    public void WindowsFileManager_OpensTheFolderInExplorer_AsBefore_AndCreatesItFirst()
    {
        var (manager, started) = ManagerWithFake();
        var folder = Path.Combine(_dir, "my vault");

        manager.OpenFolder(folder);

        Directory.Exists(folder).Should().BeTrue("a vault that was never started has no folder yet");
        var info = started.Should().ContainSingle().Subject;
        info.FileName.Should().Be("explorer.exe");
        info.Arguments.Should().Be($"\"{folder}\"");
        info.UseShellExecute.Should().BeTrue();
    }

    [Fact]
    public void WindowsFileManager_Reveal_SelectsTheItem()
    {
        var (manager, started) = ManagerWithFake();
        var file = Path.Combine(_dir, "a file.txt");

        manager.Reveal(file);

        var info = started.Should().ContainSingle().Subject;
        info.FileName.Should().Be("explorer.exe");
        info.Arguments.Should().Be($"/select,\"{file}\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WindowsFileManager_IgnoresABlankPath(string path)
    {
        var (manager, started) = ManagerWithFake();

        manager.OpenFolder(path);
        manager.Reveal(path);

        started.Should().BeEmpty();
    }

    [Fact]
    public void WindowsFileManager_AFailingStart_IsSwallowed()
    {
        var (manager, started) = ManagerWithFake(_ => throw new InvalidOperationException("no explorer"));

        var open = () => manager.OpenFolder(Path.Combine(_dir, "v"));
        var reveal = () => manager.Reveal(Path.Combine(_dir, "v"));

        open.Should().NotThrow();
        reveal.Should().NotThrow();
        started.Should().HaveCount(2);
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the repository");
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
