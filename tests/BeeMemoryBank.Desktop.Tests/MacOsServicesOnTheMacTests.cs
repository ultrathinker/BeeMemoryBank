using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The real macOS calls, headless and short: launchd with a test label and a test folder, the IOKit power assertion seen by
/// <c>pmset</c>, the IOKit sleep registration (registered and unregistered - the Mac is NEVER put to sleep), <c>osascript</c> parsing
/// hostile names. Nothing here opens a window, a Dock icon or a menu-bar item. Skipped (not failed) on any other system.
///
/// <para>The tests that start real <c>launchctl</c> jobs or write real login items work ONLY inside the folder named by the environment
/// variable <c>BMB_MAC_TEST_ROOT</c> (<see cref="MacTestRoot"/>: inside the home folder, not a temporary folder); without it they are
/// skipped with a visible reason. The machine's TMPDIR therefore never decides whether they pass: a login item may not point into a temp
/// folder, and the product refuses it. The IOKit sleep-registration tests need no folder and always run on a Mac.</para>
/// </summary>
[Collection("MacRealServices")]
public sealed class MacOsServicesOnTheMacTests
{
    private readonly ITestOutputHelper _output;

    public MacOsServicesOnTheMacTests(ITestOutputHelper output) => _output = output;

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string RealLaunchAgents => Path.Combine(Home, "Library", "LaunchAgents");

    private static CommandResult Run(string tool, params string[] args) => new ProcessCommandRunner().Run(tool, args, TimeSpan.FromSeconds(20));

    private static string[] SnapshotOf(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFileSystemEntries(folder).Select(e => $"{Path.GetFileName(e)}|{File.GetLastWriteTimeUtc(e):O}").OrderBy(s => s, StringComparer.Ordinal).ToArray()
            : [];

    // ── osascript ───────────────────────────────────────────────────────────────

    [MacRealFact]
    public void AHostileName_IsTextToTheRealAppleScript_AndRunsNothing()
    {
        // Every name is short and built here, independent of any path or of the machine's temp folder, so it is always below the notifier's
        // 200-character cap and the round trip must be EXACT. If the quoting let any of them end its literal, the evaluated value (or the
        // script itself) would differ from the name - and `touch /nonexistent/...` could not create anything anyway.
        var hostile = new[]
        {
            "say \"hi\"",
            "back\\slash and \\\" mixed \\\\",
            "\" & (do shell script \"touch /nonexistent/bmb-pwn\") & \"",
            "\\\" & (do shell script \"touch /nonexistent/bmb-pwn\") & \\\"",
            "'single' $(touch /nonexistent/bmb-pwn) `touch /nonexistent/bmb-pwn`",
            "Prof é 中文 \U0001F600",
            "a\\",
            "\\\\\"",
        };

        foreach (var name in hostile)
        {
            name.Length.Should().BeLessThan(150, "below the 200-character cap, so nothing is cut");

            // `return "<literal>"` prints the value the real AppleScript parser read out of the literal
            var result = Run(MacTools.Osascript, "-e", "return " + AppleScriptText.Quote(name));

            result.Succeeded.Should().BeTrue($"osascript must accept the literal of '{name}': {result.StandardError}");
            result.StandardOutput.TrimEnd('\n').Should().Be(name, "the text comes back exactly: it was only ever text");
        }
    }

    [MacRealFact]
    public void ANameOverTheCap_IsCut_NotAnError_AndStillOnlyText()
    {
        var name = new string('x', 190) + "\"\"\"\"\"\"\"\"\"\"" + new string('y', 300) + "\" & (do shell script \"touch /nonexistent/bmb-pwn\") & \"";

        var result = Run(MacTools.Osascript, "-e", "return " + AppleScriptText.Quote(name));

        result.Succeeded.Should().BeTrue($"a too-long name is cut, not rejected: {result.StandardError}");
        result.StandardOutput.TrimEnd('\n').Should().Be(name[..200] + "...", "the first 200 characters, then the cut mark - and nothing after the cut is read");
    }

    // ── launchd ─────────────────────────────────────────────────────────────────

    [MacRealFact]
    public void TheLoginItem_RealLaunchd_EnableLoadsItDisableBootsItOut_AndTheOwnersAgentsAreUntouched()
    {
        var folder = MacTestRoot.New("agents-real");
        MacTestRoot.RequireInside(folder);   // launchctl is only ever driven for a plist inside BMB_MAC_TEST_ROOT
        var label = "com.beememorybank.desktop.test." + Guid.NewGuid().ToString("N")[..8];
        var ownersBefore = SnapshotOf(RealLaunchAgents);
        var uid = Run("/usr/bin/id", "-u").StandardOutput.Trim();
        var autostart = new MacOsAutostart(new MacOsAutostartOptions
        {
            Label = label,
            LaunchAgentsDirectory = folder,
            ProgramArguments = ["/usr/bin/true", "--minimized"],   // starts, exits 0, does nothing
            LoadImmediately = true,
        });
        autostart.PlistPath.Should().StartWith(folder, "the plist goes to the test folder");
        label.Should().StartWith("com.beememorybank.desktop.test.", "only a test label is ever loaded");

        try
        {
            autostart.IsEnabled.Should().BeFalse();
            autostart.IsLoaded().Should().BeFalse();

            autostart.Enable();

            File.Exists(autostart.PlistPath).Should().BeTrue();
            Run("/usr/bin/plutil", "-lint", autostart.PlistPath).Succeeded.Should().BeTrue("the system's own parser accepts the plist");
            autostart.IsEnabled.Should().BeTrue();
            autostart.LastWarning.Should().BeNull();
            autostart.IsLoaded().Should().BeTrue("launchd has the test job");
            Run(MacTools.Launchctl, "print", $"gui/{uid}/{label}").Succeeded.Should().BeTrue();

            // idempotent: again leaves file and job as they are
            var before = File.ReadAllBytes(autostart.PlistPath);
            autostart.Enable();
            File.ReadAllBytes(autostart.PlistPath).Should().Equal(before);
            autostart.IsLoaded().Should().BeTrue();

            autostart.Disable();

            File.Exists(autostart.PlistPath).Should().BeFalse();
            autostart.IsEnabled.Should().BeFalse();
            autostart.IsLoaded().Should().BeFalse("booted out");
            autostart.LastWarning.Should().BeNull();
            Run(MacTools.Launchctl, "list").StandardOutput.Should().NotContain(label, "no test label is left in launchd");

            autostart.Disable();   // idempotent
        }
        finally
        {
            // safety net for THIS test's own label only: whatever happened above, the test job must not stay loaded
            Run(MacTools.Launchctl, "bootout", $"gui/{uid}/{label}");
        }

        SnapshotOf(RealLaunchAgents).Should().Equal(ownersBefore, "the owner's real ~/Library/LaunchAgents was not touched");
        Run(MacTools.Launchctl, "list").StandardOutput.Should().NotContain(label);
    }

    [MacRealFact]
    public void TheGuardOfTheRealTests_RefusesEveryFolderOutsideTheRoot()
    {
        var inside = MacTestRoot.New("guard");
        MacTestRoot.RequireInside(inside);   // fine

        foreach (var outside in new[] { RealLaunchAgents, Home, Path.GetTempPath(), "/tmp", "/", MacTestRoot.Root + "-sibling", Path.Combine(MacTestRoot.Root, "..") })
        {
            var check = () => MacTestRoot.RequireInside(outside);
            check.Should().Throw<InvalidOperationException>($"'{outside}' is outside {MacTestRoot.Variable}: launchctl must never be driven for it");
        }
    }

    // ── the plist the system's own parser sees, with the real label and a bundle path that has spaces ─────────

    [MacRealFact]
    public void TheRealLabelsPlist_ForABundlePathWithSpaces_PassesPlutil_AndReadsBackAsEnabled_WithoutLoadingAnything()
    {
        var root = MacTestRoot.New("bundle");
        var executable = Path.Combine(root, "Bee Memory Bank.app", "Contents", "MacOS", "BeeMemoryBank.Desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "#!/bin/sh\n");   // only has to exist
        var agents = Path.Combine(root, "LaunchAgents");
        // the real label, but a folder of the test's own and LoadImmediately false: launchd is never asked to load it
        var autostart = new MacOsAutostart(new MacOsAutostartOptions
        {
            LaunchAgentsDirectory = agents,
            ProgramArguments = [executable, "--minimized"],
        });

        autostart.Enable();

        autostart.Label.Should().Be("com.beememorybank.desktop");
        autostart.PlistPath.Should().Be(Path.Combine(agents, "com.beememorybank.desktop.plist"));
        Run("/usr/bin/plutil", "-lint", autostart.PlistPath).Succeeded.Should().BeTrue();
        var converted = Run("/usr/bin/plutil", "-convert", "json", "-o", "-", autostart.PlistPath);
        converted.Succeeded.Should().BeTrue(converted.StandardError);
        _output.WriteLine("plist as the system reads it: " + converted.StandardOutput.Trim());
        using var json = System.Text.Json.JsonDocument.Parse(converted.StandardOutput);
        json.RootElement.GetProperty("Label").GetString().Should().Be("com.beememorybank.desktop");
        json.RootElement.GetProperty("RunAtLoad").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("KeepAlive").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("LimitLoadToSessionType").GetString().Should().Be("Aqua");
        json.RootElement.GetProperty("ProgramArguments").EnumerateArray().Select(e => e.GetString()).Should().Equal(executable, "--minimized");
        autostart.IsEnabled.Should().BeTrue();
        Run(MacTools.Launchctl, "list").StandardOutput.Should().NotContain("com.beememorybank.desktop", "nothing was loaded");
    }

    [MacRealFact]
    public void TheRealProcessPath_OfADevelopmentRun_IsRefused_AndNothingIsWritten()
    {
        // the test host is `dotnet testhost.dll`, built in bin/Debug: exactly what a login item must never point at
        var agents = Path.Combine(MacTestRoot.New("devrun"), "LaunchAgents");
        var autostart = new MacOsAutostart(new MacOsAutostartOptions { LaunchAgentsDirectory = agents });

        var enable = () => autostart.Enable();

        enable.Should().Throw<InvalidOperationException>();
        _output.WriteLine("process path: " + Environment.ProcessPath + " -> " + ProgramLocationPolicy.Refusal(Environment.ProcessPath));
        Directory.Exists(agents).Should().BeFalse("not even the folder was made");
        autostart.IsEnabled.Should().BeFalse();
    }

    // ── power assertions ────────────────────────────────────────────────────────

    private static string[] AssertionLines(int pid)
    {
        var result = Run(MacTools.Pmset, "-g", "assertions");
        result.Succeeded.Should().BeTrue(result.StandardError);
        return result.StandardOutput.Split('\n').Where(l => l.Contains($"pid {pid}(")).ToArray();
    }

    [MacRealFact]
    public void PreventSleep_RealAssertion_IsSeenByPmsetWhileHeld_AndGoneAfterRelease()
    {
        var settings = new DesktopSettingsStore(Path.Combine(MacTestRoot.New("pmset"), "desktop-settings.json"));
        var pid = Environment.ProcessId;
        AssertionLines(pid).Should().BeEmpty("nothing of this process is held before the test");
        using var service = new MacOsPreventSleep(settings);

        service.IsEnabled = true;

        service.IsActive.Should().BeTrue();
        service.Method.Should().Be("assertion");
        var held = AssertionLines(pid);
        _output.WriteLine("pmset -g assertions, lines of this process while held: " + string.Join(" || ", held));
        held.Should().ContainSingle(l => l.Contains("PreventUserIdleSystemSleep") && l.Contains("Bee Memory Bank is keeping this Mac awake"),
            "pmset -g assertions lists the assertion with this process id and name; got: " + string.Join(" || ", held));

        service.ApplyState();   // applying again does not add a second one
        AssertionLines(pid).Should().HaveCount(1);

        service.IsEnabled = false;

        service.IsActive.Should().BeFalse();
        AssertionLines(pid).Should().BeEmpty("released: nothing of this process is held any more");
    }

    [MacRealFact]
    public void PreventSleep_TheCaffeinateFallback_IsRealAndIsStoppedOnRelease()
    {
        var settings = new DesktopSettingsStore(Path.Combine(MacTestRoot.New("caffeinate"), "desktop-settings.json"));
        var pid = Environment.ProcessId;
        var refusing = new FakeAssertions { Refuse = true };
        using var service = new MacOsPreventSleep(settings, refusing, new CaffeinateLauncher(), pid, _ => { });

        service.IsEnabled = true;

        service.Method.Should().Be("caffeinate");
        var children = Run("/usr/bin/pgrep", "-P", pid.ToString(), "-x", "caffeinate");
        _output.WriteLine("caffeinate child: " + children.StandardOutput.Trim());
        children.Succeeded.Should().BeTrue("caffeinate is a child of this process");
        var childPid = int.Parse(children.StandardOutput.Trim().Split('\n')[0]);
        var assertions = Run(MacTools.Pmset, "-g", "assertions").StandardOutput;
        _output.WriteLine("pmset lines of the caffeinate child: " + string.Join(" || ", assertions.Split('\n').Where(l => l.Contains($"pid {childPid}(caffeinate)"))));
        assertions.Should().Contain($"pid {childPid}(caffeinate)");

        service.IsEnabled = false;

        service.IsActive.Should().BeFalse();
        var gone = SpinWait.SpinUntil(() => Run("/bin/ps", "-p", childPid.ToString()).ExitCode != 0, TimeSpan.FromSeconds(5));
        gone.Should().BeTrue("the fallback child is stopped on release");
        Run(MacTools.Pmset, "-g", "assertions").StandardOutput.Should().NotContain($"pid {childPid}(caffeinate)");
    }

    // ── sleep registration ──────────────────────────────────────────────────────

    [MacOnlyFact]
    public void SleepMonitor_RealPort_RegistersAndUnregistersCleanly_WithoutAnySleep()
    {
        var notifier = new RecordingNotifier();
        var log = new List<string>();
        var locks = 0;
        var monitor = new MacOsSleepMonitor(_ => { locks++; return Task.FromResult(new SleepLockResult(true)); }, notifier,
            new IoKitPowerNotificationSource(), MacOsSleepMonitor.DefaultLockTimeout, log.Add);

        monitor.Start();

        monitor.IsRunning.Should().BeTrue("IORegisterForSystemPower worked and the run loop is pumping; log: " + string.Join(" | ", log));
        Thread.Sleep(1200);   // a few turns of the run loop with nothing to deliver
        monitor.IsRunning.Should().BeTrue();

        var stopped = Stopwatch.StartNew();
        monitor.Dispose();
        stopped.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));

        monitor.IsRunning.Should().BeFalse();
        monitor.ThreadHasExited.Should().BeTrue();
        locks.Should().Be(0, "no sleep happened, so nothing was locked");
        notifier.Notices.Should().BeEmpty();
        log.Should().BeEmpty();
    }

    [MacOnlyFact]
    public void SleepMonitor_RealPort_ManyCycles_LeakNoThreadAndNoPort()
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var threadsBefore = process.Threads.Count;
        var log = new List<string>();

        for (var i = 0; i < 25; i++)
        {
            var monitor = new MacOsSleepMonitor(_ => Task.FromResult(new SleepLockResult(true)), new RecordingNotifier(),
                new IoKitPowerNotificationSource(), MacOsSleepMonitor.DefaultLockTimeout, log.Add);
            monitor.Start();
            monitor.IsRunning.Should().BeTrue($"cycle {i}: {string.Join(" | ", log)}");
            monitor.Dispose();
            monitor.ThreadHasExited.Should().BeTrue($"cycle {i}: the monitor thread is gone");
        }

        process.Refresh();
        process.Threads.Count.Should().BeLessThan(threadsBefore + 8, "25 start/stop cycles must not leave 25 threads behind");
        log.Should().BeEmpty();
    }

    [MacOnlyFact]
    public void SleepMonitor_RealPort_DisposeRightAfterStart_AndDisposeWithoutStart_AreClean()
    {
        for (var i = 0; i < 10; i++)
        {
            var monitor = new MacOsSleepMonitor(_ => Task.FromResult(new SleepLockResult(true)), new RecordingNotifier());
            monitor.Start();
            monitor.Dispose();
            monitor.ThreadHasExited.Should().BeTrue();
        }
        new MacOsSleepMonitor(_ => Task.FromResult(new SleepLockResult(true)), new RecordingNotifier()).Dispose();
    }

    // ── the file manager and notifier are not run for real ──────────────────────

    [MacOnlyFact]
    public void TheToolsTheAdaptersCall_ExistAtTheirAbsolutePaths()
    {
        // `open` and `osascript` would open windows or banners if they were run; they are only checked to be where the code says
        foreach (var tool in new[] { MacTools.Open, MacTools.Osascript, MacTools.Launchctl, MacTools.Caffeinate, MacTools.Pmset })
            File.Exists(tool).Should().BeTrue(tool);
    }

    [Fact]
    public void TheRealRunner_StartsNothing_OffMacOS()
    {
        if (OperatingSystem.IsMacOS()) return;

        var result = new ProcessCommandRunner().Run(MacTools.Launchctl, ["list"], TimeSpan.FromSeconds(5));

        result.Succeeded.Should().BeFalse();
        result.StandardError.Should().Contain("not macOS");
    }
}

[CollectionDefinition("MacRealServices", DisableParallelization = true)]
public sealed class MacRealServicesCollection { }
