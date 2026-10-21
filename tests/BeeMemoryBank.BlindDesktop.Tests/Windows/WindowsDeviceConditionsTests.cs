using BeeMemoryBank.BlindDesktop.Windows;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>"Only on Wi-Fi or other unmetered networks" and "Pause on battery saver" as Windows answers them: the connection cost and Energy Saver, mapped without guessing.</summary>
public sealed class WindowsDeviceConditionsTests
{
    private sealed class Source : IWindowsConditionsSource
    {
        public WindowsConnectionCost? Cost { get; set; } = new(WindowsCostType.Unrestricted, false, false, false);
        public WindowsEnergySaver Saver { get; set; } = WindowsEnergySaver.Off;
        public Exception? Failure { get; set; }
        public int Reads;

        public WindowsConnectionCost? ReadConnectionCost()
        {
            Reads++;
            return Failure is { } f ? throw f : Cost;
        }

        public WindowsEnergySaver ReadEnergySaver() => Failure is { } f ? throw f : Saver;
    }

    [Theory]
    [InlineData(WindowsCostType.Unrestricted, false, false, false, false)]
    [InlineData(WindowsCostType.Fixed, false, false, false, true)]
    [InlineData(WindowsCostType.Variable, false, false, false, true)]
    [InlineData(WindowsCostType.Unrestricted, true, false, false, true)]      // roaming
    [InlineData(WindowsCostType.Unrestricted, false, true, false, true)]      // over the data limit
    [InlineData(WindowsCostType.Unrestricted, false, false, true, true)]      // close to it
    public void AConnectionCost_IsMeteredUnlessItIsUnrestricted_OrRoaming_OrOverItsLimit(WindowsCostType type, bool roaming, bool over, bool approaching, bool metered)
    {
        WindowsDeviceConditions.Metered(new WindowsConnectionCost(type, roaming, over, approaching)).Should().Be(metered);
    }

    [Fact]
    public void ACostWindowsDoesNotKnow_AndNoProfileAtAll_AreNotGuessedEitherWay()
    {
        WindowsDeviceConditions.Metered(new WindowsConnectionCost(WindowsCostType.Unknown, false, false, false)).Should().BeNull();
        WindowsDeviceConditions.Metered(null).Should().BeNull("offline, or a host with no internet profile: cannot tell");
        WindowsDeviceConditions.Metered(new WindowsConnectionCost(WindowsCostType.Unknown, true, false, false)).Should().BeTrue("roaming is metered whatever the class says");
    }

    [Theory]
    [InlineData(WindowsEnergySaver.On, true)]
    [InlineData(WindowsEnergySaver.Off, false)]
    [InlineData(WindowsEnergySaver.Disabled, false)]
    [InlineData(WindowsEnergySaver.Uninitialized, null)]
    public void EnergySaver_IsSavingPowerOnlyWhenOn(WindowsEnergySaver status, bool? saving)
    {
        WindowsDeviceConditions.SavingPower(status).Should().Be(saving);
    }

    [Fact]
    public void TheAdapter_AnswersFromItsSource()
    {
        var source = new Source { Cost = new WindowsConnectionCost(WindowsCostType.Variable, false, false, false), Saver = WindowsEnergySaver.On };

        var conditions = new WindowsDeviceConditions(source);

        conditions.IsMetered.Should().BeTrue();
        conditions.IsPowerSaving.Should().BeTrue();
    }

    [Fact]
    public void ASourceThatThrows_AnswersCannotTell_ItNeverBreaksTheLoop()
    {
        var conditions = new WindowsDeviceConditions(new Source { Failure = new InvalidOperationException("the network list is not available") });

        conditions.IsMetered.Should().BeNull();
        conditions.IsPowerSaving.Should().BeNull();
    }

    [Fact]
    public void Windows_IsAskedAtMostEveryTwoSeconds()
    {
        var time = new ManualTimeProvider();
        var source = new Source();
        var conditions = new WindowsDeviceConditions(source, time);

        _ = conditions.IsMetered;
        _ = conditions.IsPowerSaving;
        _ = conditions.IsMetered;
        source.Reads.Should().Be(1, "a download is many requests, and the guard asks before each");

        source.Cost = new WindowsConnectionCost(WindowsCostType.Fixed, false, false, false);
        conditions.IsMetered.Should().BeFalse("still the answer of a moment ago");
        time.Advance(TimeSpan.FromSeconds(3));
        conditions.IsMetered.Should().BeTrue("a new answer after two seconds");
        source.Reads.Should().Be(2);
    }

    [Fact]
    public void TheRealWinRtSource_CanBeReadOnThisMachine()
    {
        var source = new WinRtConditionsSource();

        var cost = () => source.ReadConnectionCost();
        var saver = () => source.ReadEnergySaver();

        cost.Should().NotThrow("no internet profile is null, not an exception");
        saver.Should().NotThrow();
    }
}
