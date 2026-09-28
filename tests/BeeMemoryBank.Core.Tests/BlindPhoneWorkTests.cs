using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Tests;

/// <summary>When an Android blind node may do what (plan section 10).</summary>
public class BlindPhoneWorkTests
{
    private static readonly BlindPhoneDeviceState Ideal = new(NetworkAvailable: true, Unmetered: true, Charging: true, BatteryPercent: 80);

    [Theory]
    [InlineData(BlindPhoneJob.Sync)]
    [InlineData(BlindPhoneJob.InitialLoad)]
    [InlineData(BlindPhoneJob.Backup)]
    public void OnWifiAndCharging_EverythingRuns(BlindPhoneJob job) =>
        BlindPhoneWork.WhyNot(job, Ideal).Should().BeNull();

    [Fact]
    public void Sync_RunsOnMobileDataAndBattery() =>
        BlindPhoneWork.WhyNot(BlindPhoneJob.Sync, Ideal with { Unmetered = false, Charging = false, BatteryPercent = 5 })
            .Should().BeNull();

    [Theory]
    [InlineData(BlindPhoneJob.InitialLoad)]
    [InlineData(BlindPhoneJob.Backup)]
    public void HeavyJobs_WaitForWifi_AndTheCharger(BlindPhoneJob job)
    {
        BlindPhoneWork.WhyNot(job, Ideal with { Unmetered = false }).Should().Contain("Wi-Fi");
        BlindPhoneWork.WhyNot(job, Ideal with { Charging = false }).Should().Contain("charger");
    }

    [Fact]
    public void Backup_NeverBelowTwentyPercent_EvenOnTheCharger()
    {
        BlindPhoneWork.WhyNot(BlindPhoneJob.Backup, Ideal with { BatteryPercent = 19 }).Should().Contain("20");
        BlindPhoneWork.WhyNot(BlindPhoneJob.Backup, Ideal with { BatteryPercent = 20 }).Should().BeNull();
        BlindPhoneWork.WhyNot(BlindPhoneJob.InitialLoad, Ideal with { BatteryPercent = 10 }).Should().BeNull(
            "the first load only needs Wi-Fi and the charger");
    }

    [Fact]
    public void WithoutNetwork_NothingRuns() =>
        BlindPhoneWork.WhyNot(BlindPhoneJob.Sync, Ideal with { NetworkAvailable = false }).Should().NotBeNull();
}
