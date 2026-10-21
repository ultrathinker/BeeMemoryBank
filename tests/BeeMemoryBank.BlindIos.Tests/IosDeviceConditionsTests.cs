using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// What the phone says about its network and its power, behind fakes of NWPathMonitor and NSProcessInfo: expensive (cellular, Personal Hotspot) or constrained (Low Data Mode) is metered;
/// no answer, no network or a network that needs a connection first is "cannot tell" (an account that wants an unmetered network then waits); Low Power Mode is battery saver.
/// </summary>
public class IosDeviceConditionsTests
{
    private sealed class FakePath : IIosNetworkPathSource
    {
        public IosNetworkPath? Path { get; set; }
        public int Starts { get; private set; }
        public IosNetworkPath? Current => Path;
        public void Start() => Starts++;
        public Task WaitForFirstPathAsync(TimeSpan timeout, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePower : IIosPowerSource
    {
        public bool? LowPowerModeEnabled { get; set; }
    }

    [Theory]
    [InlineData(IosPathStatus.Satisfied, false, false, false)]
    [InlineData(IosPathStatus.Satisfied, true, false, true)]
    [InlineData(IosPathStatus.Satisfied, false, true, true)]
    [InlineData(IosPathStatus.Satisfied, true, true, true)]
    public void ASatisfiedPath_IsMeteredWhenExpensiveOrConstrained(IosPathStatus status, bool expensive, bool constrained, bool metered)
    {
        IosNetworkPathRules.IsMetered(new IosNetworkPath(status, expensive, constrained)).Should().Be(metered);
    }

    [Theory]
    [InlineData(IosPathStatus.Unknown)]
    [InlineData(IosPathStatus.Unsatisfied)]
    [InlineData(IosPathStatus.Satisfiable)]
    public void NoNetworkToJudge_IsNull_EvenWhenTheFlagsAreSet(IosPathStatus status)
    {
        // Fail closed toward the data plan: "cannot tell" is a wait for an account with the switch on, never a green light.
        IosNetworkPathRules.IsMetered(new IosNetworkPath(status, IsExpensive: false, IsConstrained: false)).Should().BeNull();
        IosNetworkPathRules.IsMetered(new IosNetworkPath(status, IsExpensive: true, IsConstrained: true)).Should().BeNull();
        IosNetworkPathRules.IsMetered(null).Should().BeNull("the monitor has not answered yet");
    }

    [Fact]
    public void TheConditions_AreReadLiveAtEachQuestion_NothingIsCached()
    {
        var network = new FakePath();
        var power = new FakePower();
        var conditions = new IosDeviceConditions(network, power);

        conditions.IsMetered.Should().BeNull();
        conditions.IsPowerSaving.Should().BeNull("a power source that cannot tell stays unknown");

        network.Path = new IosNetworkPath(IosPathStatus.Satisfied, IsExpensive: false, IsConstrained: false);
        power.LowPowerModeEnabled = false;
        conditions.IsMetered.Should().BeFalse();
        conditions.IsPowerSaving.Should().BeFalse();

        network.Path = new IosNetworkPath(IosPathStatus.Satisfied, IsExpensive: true, IsConstrained: false);
        power.LowPowerModeEnabled = true;
        conditions.IsMetered.Should().BeTrue();
        conditions.IsPowerSaving.Should().BeTrue();
    }

    [Fact]
    public async Task TheCache_ReleasesAWaiterWithTheFirstPath_AndATimeoutIsNotAnError()
    {
        var cache = new IosNetworkPathCache();
        cache.Current.Should().BeNull();

        var waiting = cache.WaitForFirstAsync(TimeSpan.FromSeconds(30));
        waiting.IsCompleted.Should().BeFalse();
        cache.Update(new IosNetworkPath(IosPathStatus.Satisfied, true, false));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Current!.IsExpensive.Should().BeTrue();

        var silent = new IosNetworkPathCache();
        await silent.WaitForFirstAsync(TimeSpan.FromMilliseconds(30));   // returns, no exception
        silent.Current.Should().BeNull();

        cache.Clear();
        cache.Current.Should().BeNull("what a stopped monitor said is no longer current");
    }

    [Fact]
    public void ThePathsStatusesAndFlags_AreWhatTheHostsLimitsAskFor()
    {
        // The wiring the account's guard uses: an account with "only on unmetered networks" is held on a metered or unknown network and free on a known cheap one.
        var network = new FakePath();
        var conditions = new IosDeviceConditions(network, new FakePower { LowPowerModeEnabled = false });
        IBlindDeviceConditions asked = conditions;

        asked.IsMetered.Should().BeNull();
        network.Path = new IosNetworkPath(IosPathStatus.Satisfied, IsExpensive: false, IsConstrained: true);
        asked.IsMetered.Should().BeTrue("Low Data Mode is a request not to use data freely");
        network.Path = new IosNetworkPath(IosPathStatus.Satisfied, IsExpensive: false, IsConstrained: false);
        asked.IsMetered.Should().BeFalse();
    }
}
