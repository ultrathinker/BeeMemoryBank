using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>The tray / menu-bar tooltip sums up the accounts: "3 copies - 1 paused".</summary>
public sealed class AccountsTooltipTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private static BlindAccountsView View(FakeAccounts accounts) =>
        BlindAccountsView.Build(accounts.GetSnapshot(), Zone, new ManualTimeProvider().GetUtcNow());

    [Fact]
    public void NoAccounts_SaysTheAppIsEmpty_AndHowToStart()
    {
        AccountsTooltip.For(View(new FakeAccounts()), null).Should().Be("Bee Memory Bank - blind copy: no copies yet - open to add one");
    }

    [Fact]
    public void OneCopy_IsSingular()
    {
        var accounts = new FakeAccounts();
        accounts.AddAccount("Berlin");

        AccountsTooltip.For(View(accounts), null).Should().Be("Bee Memory Bank - blind copy: 1 copy");
    }

    [Fact]
    public void ThreeCopies_OnePaused()
    {
        var accounts = new FakeAccounts();
        accounts.AddAccount("Berlin");
        var (paris, _) = accounts.AddAccount("Paris");
        accounts.AddAccount("Oslo");
        accounts.Pauses[paris.Id] = new BlindPause(BlindPauseReasons.Traffic, Limit: 200L << 20);

        AccountsTooltip.For(View(accounts), null).Should().Be("Bee Memory Bank - blind copy: 3 copies - 1 paused");
    }

    [Fact]
    public void ACopyThatNeedsAPerson_IsCounted_AndAPausedByTheUserOneToo()
    {
        var accounts = new FakeAccounts();
        accounts.AddAccount("Berlin", status: FakeApp.StatusOf(keyLost: true));
        var (oslo, _) = accounts.AddAccount("Oslo");
        accounts.Entries[1] = oslo with { PausedByUser = true };
        accounts.Pauses[oslo.Id] = new BlindPause(BlindPauseReasons.PausedByYou);

        AccountsTooltip.For(View(accounts), null).Should().Be("Bee Memory Bank - blind copy: 2 copies - 1 paused - 1 needs attention");
    }

    [Fact]
    public void ARunningJob_LeadsTheLine()
    {
        var accounts = new FakeAccounts();
        accounts.AddAccount("Berlin");
        accounts.AddAccount("Paris");

        AccountsTooltip.For(View(accounts), "First load: 42%").Should().Be("Bee Memory Bank - blind copy: First load: 42% - 2 copies");
    }

    [Fact]
    public void ADamagedList_SaysOpenToSee()
    {
        var accounts = new FakeAccounts { RegistryProblem = "accounts.json is damaged." };

        AccountsTooltip.For(View(accounts), "ignored").Should().Be("Bee Memory Bank - blind copy: needs attention - open to see");
    }

    [Fact]
    public void TheLongestLine_StillFitsWhatTheTrayIconShows()
    {
        var accounts = new FakeAccounts();
        for (var i = 0; i < 8; i++)
        {
            var (entry, _) = accounts.AddAccount("Account " + i, status: FakeApp.StatusOf(keyLost: i % 2 == 0));
            accounts.Pauses[entry.Id] = new BlindPause(BlindPauseReasons.PausedByYou);
        }

        AccountsTooltip.For(View(accounts), "First load: 100%").Length.Should().BeLessThanOrEqualTo(120, "Windows shows 127 characters of a tooltip; the tray controller cuts at 120");
    }
}
