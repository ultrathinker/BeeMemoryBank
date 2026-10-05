using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.BlindMobile.Platforms.Android;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The owner's rule: the Android blind app never waits for the charger, the battery or the cost of the network, and an installed phone picks the
/// rule up on update. The WorkManager builder itself only compiles for Android, so besides the plan's values these tests read the real source of
/// the Android head: a literal gate put back there turns them red, which a test of the constants alone would not notice.
/// </summary>
public sealed class BlindWorkPlanTests
{
    private static string AndroidFolder() =>
        Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile", "Platforms", "Android");

    [Fact]
    public void HeavyWork_UsesOnlyTheConnectedNetworkHint()
    {
        BlindWorkPlan.RequiresConnectedNetwork.Should().BeTrue();
        BlindWorkPlan.RequiresCharging.Should().BeFalse();
        BlindWorkPlan.RequiresBatteryNotLow.Should().BeFalse();
    }

    [Fact]
    public void ExistingPeriodicWork_IsUpdated_AndRepeatedOneTimeWorkIsKept()
    {
        BlindWorkPlan.UpdateExistingPeriodicWork.Should().BeTrue();
        BlindWorkPlan.KeepExistingOneTimeWork.Should().BeTrue();
    }

    [Fact]
    public void NoSourceOfTheAndroidHead_AsksForChargingBatteryOrAnUnmeteredNetwork()
    {
        var files = Directory.GetFiles(AndroidFolder(), "*.cs", SearchOption.AllDirectories);
        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("SetRequiresCharging(true", $"{Path.GetFileName(file)} must not make a job wait for the charger");
            text.Should().NotContain("SetRequiresBatteryNotLow(true", $"{Path.GetFileName(file)} must not make a job wait for the battery");
            text.Should().NotContain("NetworkType.Unmetered", $"{Path.GetFileName(file)} must not make a job wait for Wi-Fi");
            text.Should().NotContain("NetworkType.NotRoaming", $"{Path.GetFileName(file)} must not make a job wait for a network type");
        }
    }

    [Fact]
    public void TheWorkRequests_TakeTheirRulesFromThePlan_AndTheEnqueuePoliciesFollowIt()
    {
        var text = File.ReadAllText(Path.Combine(AndroidFolder(), "BlindWork.cs"));

        text.Should().Contain("SetRequiresCharging(BlindWorkPlan.RequiresCharging)");
        text.Should().Contain("SetRequiresBatteryNotLow(BlindWorkPlan.RequiresBatteryNotLow)");
        text.Should().Contain("BlindWorkPlan.RequiresConnectedNetwork ? NetworkType.Connected! : NetworkType.NotRequired!");
        // both periodic jobs replace the constraints an older install still has
        Regex.Matches(text, @"BlindWorkPlan\.UpdateExistingPeriodicWork \? ExistingPeriodicWorkPolicy\.Update!").Count.Should().Be(2);
        // "Sync now" / the first load right after pairing: a second request never kills a running job
        Regex.Matches(text, @"BlindWorkPlan\.KeepExistingOneTimeWork \? ExistingWorkPolicy\.Keep!").Count.Should().Be(2);
    }
}
