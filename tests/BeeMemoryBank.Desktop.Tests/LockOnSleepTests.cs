using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// "Lock the vault when this computer sleeps" is a setting, off by default, read at each sleep event. The setting itself, and both sleep
/// monitors: with it off a sleep makes no lock request and shows no notice; with it on the behaviour is the one the monitors always had.
/// </summary>
public sealed class LockOnSleepSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-lock-on-sleep-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "desktop-settings.json");

    public LockOnSleepSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort, our own temp folder */ }
    }

    private LockOnSleepSetting Open() => new(new DesktopSettingsStore(SettingsPath));

    [Fact]
    public void TheDefaultIsOff_WithNoSettingsFile()
    {
        File.Exists(SettingsPath).Should().BeFalse();

        Open().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKey_ReadsAsOff_AndKeepsItsOtherSettings()
    {
        File.WriteAllText(SettingsPath, "{ \"preventSleep\": true, \"autoCheckUpdates\": false }");
        var setting = Open();

        setting.IsEnabled.Should().BeFalse("an installation from before the setting existed must not start locking on sleep");

        setting.IsEnabled = true;
        var store = new DesktopSettingsStore(SettingsPath);
        store.GetBool("preventSleep", false).Should().BeTrue();
        store.GetBool("autoCheckUpdates", true).Should().BeFalse();
        store.GetBool(LockOnSleepSetting.SettingKey, false).Should().BeTrue();
    }

    [Fact]
    public void TheValueSurvivesARoundTrip_BothWays()
    {
        Open().IsEnabled = true;
        Open().IsEnabled.Should().BeTrue("a new instance reads what the file holds");

        Open().IsEnabled = false;
        Open().IsEnabled.Should().BeFalse();
        File.ReadAllText(SettingsPath).Should().Contain("\"lockOnSleep\": false", "off is stored explicitly, not by removing the key");
    }

    [Fact]
    public void AFileThatCannotBeRead_ReadsAsOff()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        Open().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void TheLabelAndTheHint_AreTheAgreedEnglishTexts()
    {
        LockOnSleepSetting.Label.Should().Be("Lock the vault when this computer sleeps");
        LockOnSleepSetting.Hint.Should().Be("After waking up you sign in again.");
    }
}

public sealed class PowerEventsServiceLockOnSleepTests
{
    private int _requests;
    private int _notices;

    private PowerEventsService Make(Func<bool>? enabled) => new(() => _requests++, enabled, () => _notices++);

    [Fact]
    public void WithTheSettingOff_ASleepMakesNoRequestAndShowsNoNotice()
    {
        Make(() => false).HandleSuspend();

        _requests.Should().Be(0);
        _notices.Should().Be(0);
    }

    [Fact]
    public void WithTheSettingOn_ASleepShowsTheNoticeAndMakesTheRequest()
    {
        Make(() => true).HandleSuspend();

        _requests.Should().Be(1);
        _notices.Should().Be(1);
    }

    [Fact]
    public void TheSettingIsReadAtEachSleep_NotOnceAtStart()
    {
        var enabled = false;
        var service = Make(() => enabled);

        service.HandleSuspend();
        enabled = true;
        service.HandleSuspend();
        enabled = false;
        service.HandleSuspend();

        _requests.Should().Be(1, "only the sleep in the middle had it on");
        _notices.Should().Be(1);
    }

    [Fact]
    public void ASettingThatCannotBeRead_CountsAsOff()
    {
        Make(() => throw new IOException("disk")).HandleSuspend();

        _requests.Should().Be(0);
        _notices.Should().Be(0);
    }

    [Fact]
    public void WithoutAPredicate_TheServiceBehavesAsItAlwaysDid()
    {
        Make(null).HandleSuspend();

        _requests.Should().Be(1);
        _notices.Should().Be(1);
    }
}

public sealed class MacOsSleepMonitorLockOnSleepTests
{
    private const uint WillSleep = 0xE0000280;

    private readonly FakePowerPort _port = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly ConcurrentQueue<string> _log = new();
    private int _requests;

    private MacOsSleepMonitor Make(Func<bool>? enabled) =>
        new(_ =>
        {
            _requests++;
            return Task.FromResult(new SleepLockResult(true));
        }, _notifier, _port, TimeSpan.FromSeconds(5), _log.Enqueue, enabled);

    [Fact]
    public void WithTheSettingOff_ASleepMakesNoRequest_ShowsNoNotice_LogsOnce_AndStillAllowsTheSleepOnce()
    {
        using var monitor = Make(() => false);
        monitor.Start();

        _port.Send(WillSleep, 11);

        _requests.Should().Be(0);
        _notifier.Notices.Should().BeEmpty();
        _log.Should().ContainSingle().Which.Should().Contain("Lock on sleep is off");
        _port.Allowed.Should().Equal(new IntPtr(11));
    }

    [Fact]
    public void WithTheSettingOn_ASleepMakesTheRequest_ShowsTheNotice_AndAllowsTheSleepOnce()
    {
        using var monitor = Make(() => true);
        monitor.Start();

        _port.Send(WillSleep, 12);

        _requests.Should().Be(1);
        _notifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("The vault was locked");
        _port.Allowed.Should().Equal(new IntPtr(12));
    }

    [Fact]
    public void TheSettingIsReadAtEachSleep_NotOnceAtStart()
    {
        var enabled = false;
        using var monitor = Make(() => enabled);
        monitor.Start();

        _port.Send(WillSleep, 1);
        enabled = true;
        _port.Send(WillSleep, 2);
        enabled = false;
        _port.Send(WillSleep, 3);

        _requests.Should().Be(1, "only the second sleep had the setting on");
        _notifier.Notices.Should().ContainSingle();
        _port.Allowed.Select(p => (int)p).Should().Equal(new[] { 1, 2, 3 }, "every sleep is allowed, whatever the setting says");
    }

    [Fact]
    public void WhenTheRegistrationFails_TheWillNotBeLockedNoticeIsShownOnlyWithTheSettingOn()
    {
        var off = new RecordingNotifier();
        _port.OpenFails = new InvalidOperationException("no port");
        using (var monitor = new MacOsSleepMonitor(_ => Task.FromResult(new SleepLockResult(true)), off, _port, TimeSpan.FromSeconds(1), _log.Enqueue, () => false))
            monitor.Start();
        off.Notices.Should().BeEmpty("the person does not want the vault locked on sleep, so there is nothing to warn about");

        var on = new RecordingNotifier();
        using (var monitor = new MacOsSleepMonitor(_ => Task.FromResult(new SleepLockResult(true)), on, _port, TimeSpan.FromSeconds(1), _log.Enqueue, () => true))
            monitor.Start();
        on.Notices.Should().ContainSingle().Which.Message.Should().Contain("will NOT be locked");
    }

    [Fact]
    public void ASettingThatCannotBeRead_CountsAsOff()
    {
        using var monitor = Make(() => throw new IOException("disk"));
        monitor.Start();

        _port.Send(WillSleep, 5);

        _requests.Should().Be(0);
        _notifier.Notices.Should().BeEmpty();
        _port.Allowed.Should().Equal(new IntPtr(5));
    }
}
