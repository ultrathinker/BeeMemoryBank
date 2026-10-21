using BeeMemoryBank.BlindIos.Services;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>What the Accounts page says about background time: nothing without an account, the reason when iOS gives none, and the honest price of several accounts.</summary>
public class IosAccountsLinesTests
{
    [Fact]
    public void WithoutAnAccount_NothingIsSaidAboutBackgroundTime()
    {
        IosAccountsLines.BackgroundNote(0, refreshAvailable: true, "Available", lowPowerMode: false).Should().BeNull();
    }

    [Fact]
    public void WhenIosGivesNoBackgroundTime_TheReasonIsSaid_AndHowToChangeIt()
    {
        IosAccountsLines.BackgroundNote(1, refreshAvailable: false, "Denied", lowPowerMode: false)
            .Should().Contain("Background App Refresh is off").And.Contain("Denied").And.Contain("the copy syncs only while the app is open");
        IosAccountsLines.BackgroundNote(3, refreshAvailable: false, "Restricted", lowPowerMode: true)
            .Should().Contain("the copies sync only while the app is open", "the switch that is off comes before Low Power Mode");
        IosAccountsLines.BackgroundNote(2, refreshAvailable: true, "Available", lowPowerMode: true).Should().Contain("Low Power Mode is on").And.Contain("the copies");
    }

    [Fact]
    public void SeveralAccounts_ShareTheShortTurnsIosGives_AndOnlyTheOpenAppSyncsThemAll()
    {
        var one = IosAccountsLines.BackgroundNote(1, refreshAvailable: true, "Available", lowPowerMode: false)!;
        one.Should().Contain("iOS decides").And.NotContain("less often");

        var three = IosAccountsLines.BackgroundNote(3, refreshAvailable: true, "Available", lowPowerMode: false)!;
        three.Should().Contain("3 accounts").And.Contain("less often").And.Contain("waited longest first").And.Contain("Open the app to sync all of them");
    }

    [Fact]
    public void TheNotificationsLine_IsOnlyForAPhoneWithAPairedAccount_WhoseAlertsAreOff()
    {
        IosAccountsLines.AlertsNote(anyAccountPaired: true, alertsAllowed: false).Should().Contain("Notifications are off");
        IosAccountsLines.AlertsNote(anyAccountPaired: false, alertsAllowed: false).Should().BeNull();
        IosAccountsLines.AlertsNote(anyAccountPaired: true, alertsAllowed: true).Should().BeNull();
        IosAccountsLines.AlertsNote(anyAccountPaired: true, alertsAllowed: null).Should().BeNull("iOS has not answered yet");
    }
}
