using System;
using System.Collections.Generic;
using System.IO;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

internal sealed class FakeAssertions : IPowerAssertions
{
    public bool Refuse { get; set; }
    public List<string> Created { get; } = [];
    public List<uint> Released { get; } = [];
    public bool ThrowOnRelease { get; set; }
    private uint _next = 100;

    public bool TryCreate(string name, out uint assertionId, out string? error)
    {
        if (Refuse)
        {
            assertionId = 0;
            error = "refused by the fake";
            return false;
        }
        Created.Add(name);
        assertionId = ++_next;
        error = null;
        return true;
    }

    public void Release(uint assertionId)
    {
        Released.Add(assertionId);
        if (ThrowOnRelease) throw new InvalidOperationException("release failed");
    }
}

internal sealed class FakeCaffeinate : ICaffeinateLauncher
{
    public bool Fail { get; set; }
    public List<int> StartedFor { get; } = [];
    public int Running { get; private set; }
    public int Stopped { get; private set; }

    public IDisposable? Start(int watchedProcessId, out string? error)
    {
        if (Fail)
        {
            error = "no caffeinate here";
            return null;
        }
        StartedFor.Add(watchedProcessId);
        Running++;
        error = null;
        return new Handle(this);
    }

    private sealed class Handle(FakeCaffeinate owner) : IDisposable
    {
        public void Dispose()
        {
            owner.Running--;
            owner.Stopped++;
        }
    }
}

/// <summary>The Mac's "keep awake" setting. The real IOKit assertion and <c>pmset</c> are checked on the Mac (<see cref="MacOsServicesOnTheMacTests"/>).</summary>
public sealed class MacOsPreventSleepTests
{
    private readonly string _settingsPath = Path.Combine(TestScratch.New("preventsleep"), "desktop-settings.json");
    private readonly FakeAssertions _assertions = new();
    private readonly FakeCaffeinate _caffeinate = new();
    private readonly List<string> _log = [];

    private MacOsPreventSleep Make(string? settingsPath = null) =>
        new(new DesktopSettingsStore(settingsPath ?? _settingsPath), _assertions, _caffeinate, processId: 4242, _log.Add);

    [Fact]
    public void ByDefault_NothingIsHeld()
    {
        var service = Make();

        service.IsEnabled.Should().BeFalse();
        service.ApplyState();

        service.IsActive.Should().BeFalse();
        _assertions.Created.Should().BeEmpty();
        _caffeinate.StartedFor.Should().BeEmpty();
    }

    [Fact]
    public void TurningItOn_TakesOneAssertion_AndSavesTheSettingUnderTheWindowsKey()
    {
        var service = Make();

        service.IsEnabled = true;

        service.IsActive.Should().BeTrue();
        service.Method.Should().Be("assertion");
        _assertions.Created.Should().ContainSingle().Which.Should().Contain("Bee Memory Bank");
        new DesktopSettingsStore(_settingsPath).GetBool("preventSleep", false).Should().BeTrue("the same setting key as the Windows service");
    }

    [Fact]
    public void ApplyingTwice_DoesNotTakeASecondAssertion()
    {
        var service = Make();
        service.IsEnabled = true;

        service.ApplyState();
        service.ApplyState();
        service.IsEnabled = true;

        _assertions.Created.Should().HaveCount(1);
    }

    [Fact]
    public void TurningItOff_ReleasesExactlyTheAssertionItTook()
    {
        var service = Make();
        service.IsEnabled = true;

        service.IsEnabled = false;
        service.IsEnabled = false;
        service.ApplyState();

        service.IsActive.Should().BeFalse();
        _assertions.Released.Should().Equal(101u);
        new DesktopSettingsStore(_settingsPath).GetBool("preventSleep", true).Should().BeFalse();
    }

    [Fact]
    public void TheSettingIsAppliedAtStart_WhenItWasOn()
    {
        Make().IsEnabled = true;
        var next = Make();

        next.IsEnabled.Should().BeTrue("loaded from the file");
        next.IsActive.Should().BeFalse("loading the setting holds nothing until it is applied");
        next.ApplyState();
        next.IsActive.Should().BeTrue();
    }

    [Fact]
    public void OnExit_TheAssertionIsReleased_ButTheSettingStays()
    {
        var service = Make();
        service.IsEnabled = true;

        service.DisableSleepPreventionOnly();

        service.IsActive.Should().BeFalse();
        _assertions.Released.Should().HaveCount(1);
        new DesktopSettingsStore(_settingsPath).GetBool("preventSleep", false).Should().BeTrue("the person's choice is kept for the next start");
        service.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Dispose_Releases_AndDoingItTwiceIsHarmless()
    {
        var service = Make();
        service.IsEnabled = true;

        service.Dispose();
        service.Dispose();

        _assertions.Released.Should().HaveCount(1);
    }

    [Fact]
    public void WhenTheAssertionIsRefused_CaffeinateWatchingThisProcessIsTheFallback()
    {
        _assertions.Refuse = true;
        var service = Make();

        service.IsEnabled = true;

        service.IsActive.Should().BeTrue();
        service.Method.Should().Be("caffeinate");
        _caffeinate.StartedFor.Should().Equal(4242);
        _log.Should().Contain(l => l.Contains("refused by the fake") && l.Contains("falling back to caffeinate"));

        service.IsEnabled = false;
        _caffeinate.Running.Should().Be(0);
        _caffeinate.Stopped.Should().Be(1);
    }

    [Fact]
    public void CaffeinateIsNotStarted_WhenTheAssertionWorks()
    {
        var service = Make();

        service.IsEnabled = true;

        _caffeinate.StartedFor.Should().BeEmpty("the fallback is only for a refused assertion");
    }

    [Fact]
    public void WhenNothingWorks_ItSaysSo_AndDoesNotClaimToBeActive()
    {
        _assertions.Refuse = true;
        _caffeinate.Fail = true;
        var service = Make();

        service.IsEnabled = true;

        service.IsActive.Should().BeFalse();
        service.IsEnabled.Should().BeTrue("the setting is the person's choice; it is not reset because the system said no");
        _log.Should().Contain(l => l.Contains("NOT actually active"));
        service.IsEnabled = false;   // and turning it off afterwards is harmless
    }

    [Fact]
    public void AFailingRelease_IsLogged_AndTheServiceIsOffAnyway()
    {
        var service = Make();
        service.IsEnabled = true;
        _assertions.ThrowOnRelease = true;

        var off = () => service.IsEnabled = false;

        off.Should().NotThrow();
        service.IsActive.Should().BeFalse();
        _log.Should().Contain(l => l.Contains("release failed"));
    }

    [Fact]
    public void ASettingsFileThatCannotBeWritten_DoesNotStopTheServiceFromWorking()
    {
        var blocked = Path.Combine(TestScratch.New("preventsleep-blocked"), "settings.json");
        Directory.CreateDirectory(blocked);   // a folder where the file should be
        var service = Make(blocked);

        var on = () => service.IsEnabled = true;

        on.Should().NotThrow();
        service.IsActive.Should().BeTrue();
        _log.Should().Contain(l => l.Contains("Error saving settings"));
    }
}
