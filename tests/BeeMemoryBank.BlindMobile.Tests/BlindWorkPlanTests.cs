using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.BlindMobile.Platforms.Android;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The rules of the Android blind app's work requests. The phone never waits for the charger and never for more than a connected network, EXCEPT what the accounts themselves ask for: when every account
/// that uses the network is "only on unmetered networks" the two periodic requests wait for one, and when every one pauses on battery saver they wait for a battery that is not low
/// (<see cref="BlindWorkPlan.For"/>; the table of it is in <c>BlindHeadCompositionTests</c>). The WorkManager builder itself only compiles for Android, so besides the plan's values these tests read the real source of
/// the Android head: a literal gate put back there turns them red, which a test of the constants alone would not notice.
/// </summary>
public sealed class BlindWorkPlanTests
{
    private static string AndroidFolder() =>
        Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile", "Platforms", "Android");

    private static string WorkSource() => File.ReadAllText(Path.Combine(AndroidFolder(), "BlindWork.cs")).ReplaceLineEndings("\n");

    /// <summary>The text of one member of BlindWork.cs: from its declaration to the next member of the same indent.</summary>
    private static string MethodOf(string signature)
    {
        var text = WorkSource();
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{signature} exists");
        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return text[start..(end < 0 ? text.Length : end)];
    }

    [Fact]
    public void TheRulesThatDoNotDependOnTheAccounts_AreAConnectedNetworkAndNeverTheCharger()
    {
        BlindWorkPlan.RequiresConnectedNetwork.Should().BeTrue();
        BlindWorkPlan.RequiresCharging.Should().BeFalse();
    }

    [Fact]
    public void ExistingPeriodicWork_IsUpdated_AndRepeatedOneTimeWorkIsKept()
    {
        BlindWorkPlan.UpdateExistingPeriodicWork.Should().BeTrue();
        BlindWorkPlan.KeepExistingOneTimeWork.Should().BeTrue();
    }

    [Fact]
    public void TheFourUniqueWorkNames_AreTheOnesOf26_NoWorkIsNamedPerAccount()
    {
        var text = WorkSource();

        var names = Regex.Matches(text, @"private const string \w+Work = ""([^""]+)"";").Select(m => m.Groups[1].Value).ToList();

        names.Should().BeEquivalentTo(["bmb_blind_sync", "bmb_blind_heavy", "bmb_blind_heavy_now", "bmb_blind_sync_now"],
            "an installed 2.6.4 copy keeps its queued work; one job for the device, not one per account");
        text.Should().NotContain("\"bmb_blind_sync.", "no work name carries an account id");
        text.Should().NotContain("CancelUniqueWork", "the device's jobs are not cancelled for one account's sake (a removal stops only that account's own work)");
    }

    [Fact]
    public void OnlyTheWorkBuilder_WaitsForAnUnmeteredNetworkOrABatteryThatIsNotLow_ThroughThePlansRequirements()
    {
        foreach (var file in Directory.GetFiles(AndroidFolder(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            text.Should().NotContain("SetRequiresCharging(true", $"{name} must not make a job wait for the charger");
            text.Should().NotContain("NetworkType.NotRoaming", $"{name} must not make a job wait for a network type");
            if (name == "BlindWork.cs") continue;
            text.Should().NotContain("NetworkType.Unmetered", $"{name} must not make a job wait for Wi-Fi");
            text.Should().NotContain("SetRequiresBatteryNotLow(", $"{name} must not make a job wait for the battery");
        }

        var work = WorkSource();
        work.Should().Contain("r.UnmeteredNetwork ? NetworkType.Unmetered! : NetworkType.Connected!");
        work.Should().Contain("SetRequiresBatteryNotLow(r.BatteryNotLow)");
        work.Should().Contain("SetRequiresCharging(BlindWorkPlan.RequiresCharging)");
        work.Should().Contain("BlindWorkPlan.RequiresConnectedNetwork ? NetworkType.Connected! : NetworkType.NotRequired!");
    }

    [Fact]
    public void TheTwoPeriodicRequests_TakeTheAccountsRequirements_TheTwoRequestsThePersonMakes_WaitForAConnectedNetworkOnly()
    {
        var ensure = MethodOf("public static void Ensure(");
        Regex.Matches(ensure, @"\.SetConstraints\(PeriodicConstraints\(wanted\)\)").Count.Should().Be(2, "the sync job and the long job");

        foreach (var request in new[] { "public static void RunHeavyNow(", "public static void SyncNow(" })
        {
            var body = MethodOf(request);
            body.Should().Contain(".SetConstraints(AskedForConstraints())", "the guard answers a switch at once, where the person can read it");
            body.Should().NotContain("PeriodicConstraints").And.NotContain("Unmetered");
        }
        Regex.Matches(WorkSource(), @"BlindWorkPlan\.KeepExistingOneTimeWork \? ExistingWorkPolicy\.Keep!").Count.Should().Be(2,
            "\"Sync now\" / the first load right after pairing: a second request never kills a running job");
    }

    [Fact]
    public void KnownRequirementsReplaceTheQueuedRequests_UnknownOnesNeverLoosenThem()
    {
        var text = WorkSource();

        text.Should().Contain("known && BlindWorkPlan.UpdateExistingPeriodicWork ? ExistingPeriodicWorkPolicy.Update! : ExistingPeriodicWorkPolicy.Keep!",
            "a changed switch (or an update of the app) is picked up; the boot receiver, which knows no accounts, keeps what is queued");
        text.Should().Contain("if (requirements is { } known && _applied == known) return;", "the system is not asked again for what it already has");
    }

    [Fact]
    public void TheWorkers_HandThePassToTheDevicesSchedulers_TheSyncOneNeverRunsALongJob()
    {
        var text = WorkSource();
        var sync = text[text.IndexOf("class BlindSyncWorker", StringComparison.Ordinal)..text.IndexOf("class BlindHeavyWorker", StringComparison.Ordinal)];
        var heavy = text[text.IndexOf("class BlindHeavyWorker", StringComparison.Ordinal)..text.IndexOf("class BlindBackupService", StringComparison.Ordinal)];

        sync.Should().Contain("device.StartAsync(token)").And.Contain("device.SyncScheduler.TickAsync(token)");
        sync.Should().NotContain("HeavyScheduler").And.NotContain("SetForegroundAsync", "a sync pass is short and shows no notification");
        heavy.Should().Contain("device.StartAsync(token)").And.Contain("device.HeavyScheduler.TickAsync(token)").And.Contain("SetForegroundAsync(");
        heavy.Should().NotContain("SyncScheduler");

        // the pass of a job is the WorkManager period, so every pass looks at every account
        BlindDevice.SyncOptions.RunSync.Should().BeTrue();
        BlindDevice.SyncOptions.RunHeavy.Should().BeFalse();
        BlindDevice.SyncOptions.SyncEvery.Should().Be(TimeSpan.Zero);
        BlindDevice.HeavyOptions.RunHeavy.Should().BeTrue();
        BlindDevice.HeavyOptions.RunSync.Should().BeFalse();
        BlindDevice.HeavyOptions.HeavyEvery.Should().Be(TimeSpan.Zero);
    }
}
